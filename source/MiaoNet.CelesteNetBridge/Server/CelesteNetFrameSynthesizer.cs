using System.Diagnostics.CodeAnalysis;

using MiaoNet.CelesteNetBridge.Protocol;
using MiaoNet.Shared;

namespace MiaoNet.CelesteNetBridge.Server;

/// <summary>
/// Builds CelesteNet <c>playerFrame</c> payloads for remote players from MiaoNet's own per-tick player
/// data, so the local real client does not need the relayed frames.
/// </summary>
/// <remarks>
/// <para>
/// The synthesizer is deliberately free of Celeste and CelesteNet types: the unit test project links
/// it directly, and the bridge translates MiaoNet's data into <see cref="CelesteNetPlayerFrameInput"/>.
/// Everything that is per-player state lives here:
/// </para>
/// <list type="bullet">
/// <item>the appearance template, taken from the newest real <c>playerFrame</c> (hair colors, hair
/// texture, sprite tint, follower and held-item styling) and re-applied to every synthesised frame;</item>
/// <item>the appearance projection and its keyframe flag: a real frame whose projection changed is kept
/// as the one frame worth forwarding to synthesising peers, instead of the whole per-tick stream;</item>
/// <item>the animation table from the relayed <c>playerGraphics</c>, which turns MiaoNet's animation
/// names into the indices CelesteNet's ghost expects;</item>
/// <item>death, dash and speed state that MiaoNet's deltas do not carry per frame;</item>
/// <item>a tombstone per player: once the player is announced as left, no synthesised frame may reach
/// the local client any more, because its reference has been freed and the client would drop the
/// connection on an unknown reference.</item>
/// </list>
/// <para>
/// Frames are queued per input and drained once per engine frame: MiaoNet can deliver several deltas
/// for one player in a single engine frame after a network backlog, and only the newest of them is
/// worth encoding.
/// </para>
/// </remarks>
public sealed class CelesteNetFrameSynthesizer
{
    /// <summary>CelesteNet's server clamps followers to this many; MiaoNet never sends more either.</summary>
    private const int MaxFollowers = 12;

    /// <summary>Used when a caller cannot provide a frame time; MiaoNet and CelesteNet both tick at 60 Hz.</summary>
    private const float DefaultDeltaSeconds = 1f / 60f;

    /// <summary>Fallback hair texture: the first bangs frame of the vanilla player sprite.</summary>
    private const string DefaultHairTexture = "characters/player/bangs0";

    /// <summary>The sprite CelesteNet uses for a held Theo crystal.</summary>
    private const string TheoHoldingSprite = "theo_crystal";

    /// <summary>The sprite CelesteNet uses for a held jelly.</summary>
    private const string JellyHoldingSprite = "glider";

    /// <summary>Depth CelesteNet gives a follow/hold entity whose real one is unknown.</summary>
    private const int FallbackEntityDepth = -1000000;

    /// <summary>
    /// At most one appearance keyframe per player per this many seconds. Without a limit, a skin mod
    /// that changes hair colors every frame would turn the synthesis stream back into the full frame
    /// stream this design exists to avoid.
    /// </summary>
    public const double AppearanceKeyframeMinIntervalSeconds = 0.25;

    // MiaoNet's default hair palette (PlayerGraphicsInfo.Default): the only source when no real frame
    // has arrived yet. Lengths match PlayerGraphicsInfo.GetHairInfo; above two dashes it is dash two.
    private static readonly CelesteNetColor Dash0HairColor = new(0x44, 0xB7, 0xFF);
    private static readonly CelesteNetColor Dash1HairColor = new(0xAC, 0x32, 0x32);
    private static readonly CelesteNetColor Dash2HairColor = new(0xFF, 0x6D, 0xEF);
    private static readonly CelesteNetColor FeatherHairColor = new(0xF2, 0xEB, 0x6D);

    private readonly Dictionary<uint, PlayerState> players = [];
    private readonly Dictionary<uint, PendingInput> pending = [];
    private readonly HashSet<uint> tombstoned = [];

    /// <summary>
    /// Records a real frame from <paramref name="playerID"/>. Its appearance projection becomes the
    /// template for synthesised frames, and a changed projection becomes the player's appearance
    /// keyframe.
    /// </summary>
    /// <remarks>
    /// A frame whose projection is unchanged is ignored: position, scale, facing, animation, speed and
    /// death are taken from MiaoNet every tick anyway, so there is no reason to churn the template or
    /// queue a keyframe for them.
    /// </remarks>
    public void AcceptAppearance(uint playerID, CelesteNetPlayerFrame appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        // The ref in the frame has to name the same player; a mismatch would style the wrong ghost.
        if (appearance.PlayerID != playerID || tombstoned.Contains(playerID))
        {
            return;
        }

        PlayerState state = GetState(playerID);
        AppearanceProjection projection = new(appearance);
        if (state.Appearance is not null && state.Projection is { } previous && previous.Matches(projection))
        {
            return;
        }

        state.Appearance = appearance;
        state.Projection = projection;
        state.AppearanceKeyframePending = true;
    }

    /// <summary>Records the newest relayed <c>playerGraphics</c>, which carries the animation table.</summary>
    public void AcceptGraphics(uint playerID, CelesteNetPlayerGraphics graphics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        if (tombstoned.Contains(playerID))
        {
            return;
        }

        GetState(playerID).Graphics = graphics;
    }

    /// <summary>
    /// Hands out the appearance keyframe only when the per-player rate limit allows it. A change that
    /// arrives too soon stays pending, so the latest appearance is sent once the interval has passed.
    /// </summary>
    /// <param name="playerID">The player whose appearance should be sent.</param>
    /// <param name="nowSeconds">A monotonic timestamp in seconds; the bridge passes the process uptime.</param>
    /// <param name="appearance">The frame to send, when one is due.</param>
    public bool TryTakeAppearanceKeyframe(
        uint playerID,
        double nowSeconds,
        [NotNullWhen(true)] out CelesteNetPlayerFrame? appearance
    )
        => TryTakeAppearanceKeyframe(
            playerID, nowSeconds, AppearanceKeyframeMinIntervalSeconds, out appearance
        );

    /// <summary>
    /// Hands out the appearance keyframe if the change is older than <paramref name="minIntervalSeconds"/>.
    /// The first frame of a player is always due: the peer has no appearance for them yet.
    /// </summary>
    public bool TryTakeAppearanceKeyframe(
        uint playerID,
        double nowSeconds,
        double minIntervalSeconds,
        [NotNullWhen(true)] out CelesteNetPlayerFrame? appearance
    )
    {
        appearance = null;
        if (!players.TryGetValue(playerID, out PlayerState? state)
            || !state.AppearanceKeyframePending
            || state.Appearance is null) {
            return false;
        }

        if (state.LastAppearanceKeyframeSeconds is double last
            && nowSeconds - last < minIntervalSeconds) {
            return false;
        }

        state.LastAppearanceKeyframeSeconds = nowSeconds;
        state.AppearanceKeyframePending = false;
        appearance = state.Appearance;
        return true;
    }

    /// <summary>Whether the player's appearance changed since the last keyframe was taken.</summary>
    public bool IsAppearanceKeyframePending(uint playerID)
        => players.TryGetValue(playerID, out PlayerState? state)
            && state.AppearanceKeyframePending
            && state.Appearance is not null;

    /// <summary>
    /// Marks every known appearance as due once more. A level entry resends appearance even when it did
    /// not change: a peer may have joined while our client had nothing new to show.
    /// </summary>
    public void MarkAppearanceKeyframesPending()
    {
        foreach (PlayerState state in players.Values)
        {
            if (state.Appearance is not null)
            {
                state.AppearanceKeyframePending = true;
            }
        }
    }

    /// <summary>
    /// Carries a MiaoNet Die/Respawn edge into the frames. CelesteNet only triggers the death effect on
    /// a rising edge, so the state has to be remembered rather than taken from the delta every tick.
    /// </summary>
    public void SetDead(uint playerID, bool dead)
    {
        if (tombstoned.Contains(playerID))
        {
            return;
        }

        GetState(playerID).Dead = dead;
    }

    /// <summary>
    /// Marks a player as gone: drops everything pending and refuses every later frame. The bridge calls
    /// this right before it queues the player's dead <c>playerInfo</c>, so no synthesised frame can be
    /// left behind to arrive after the client freed the player's reference.
    /// </summary>
    public void Tombstone(uint playerID)
    {
        tombstoned.Add(playerID);
        players.Remove(playerID);
        pending.Remove(playerID);
    }

    /// <summary>Allows frames again for a player that was tombstoned and has been announced anew.</summary>
    public void Revive(uint playerID)
    {
        tombstoned.Remove(playerID);
        if (players.TryGetValue(playerID, out PlayerState? state))
        {
            ResetTransient(state);
        }
    }

    /// <summary>Whether frames for this player are refused because it was tombstoned.</summary>
    public bool IsTombstoned(uint playerID) => tombstoned.Contains(playerID);

    /// <summary>
    /// Keeps the newest input of one player for the current engine frame. Returns false when the
    /// synthesizer refuses the player (a tombstone is in effect).
    /// </summary>
    public bool Queue(CelesteNetPlayerFrameInput input, ulong engineFrameCounter)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (tombstoned.Contains(input.PlayerID))
        {
            return false;
        }

        pending[input.PlayerID] = new(engineFrameCounter, input);
        return true;
    }

    /// <summary>
    /// Encodes and returns every queued frame that was captured before
    /// <paramref name="engineFrameCounter"/>, at most one per player.
    /// </summary>
    /// <remarks>
    /// A frame queued during the current engine frame stays pending: it is encoded on a later frame,
    /// which is what coalesces a backlog burst of deltas into one frame per player per engine frame.
    /// </remarks>
    public IReadOnlyList<CelesteNetSynthesisedFrame> Drain(ulong engineFrameCounter)
    {
        if (pending.Count == 0)
        {
            return [];
        }

        List<uint>? ready = null;
        foreach ((uint playerID, PendingInput input) in pending)
        {
            if (input.EngineFrameCounter >= engineFrameCounter)
            {
                continue;
            }

            (ready ??= []).Add(playerID);
        }

        if (ready is null)
        {
            return [];
        }

        List<CelesteNetSynthesisedFrame> frames = new(ready.Count);
        foreach (uint playerID in ready)
        {
            PendingInput input = pending[playerID];
            pending.Remove(playerID);
            if (tombstoned.Contains(playerID))
            {
                continue;
            }

            frames.Add(new(playerID, CelesteNetPlayerFrame.Encode(Build(playerID, input.Input))));
        }

        return frames;
    }

    /// <summary>Drops every queued-but-not-yet-encoded frame, for settings and level changes.</summary>
    public void ClearPending() => pending.Clear();

    /// <summary>
    /// Forgets speed, dash and death state for every player, for level and map changes. The appearance
    /// template and a pending appearance keyframe are deliberately kept: the player did not change.
    /// </summary>
    public void ResetTransientState()
    {
        foreach (PlayerState state in players.Values)
        {
            ResetTransient(state);
        }
    }

    /// <summary>
    /// Forgets speed, dash and death state for one player, and drops a frame captured in the room it
    /// just left. Called when a remote player changes room inside our map. Appearance is kept.
    /// </summary>
    public void ResetTransientState(uint playerID)
    {
        pending.Remove(playerID);
        if (players.TryGetValue(playerID, out PlayerState? state))
        {
            ResetTransient(state);
        }
    }

    private static void ResetTransient(PlayerState state)
    {
        state.Dead = false;
        state.HasPositionSample = false;
        state.WasDashing = false;
        state.DashWasB = false;
    }

    private PlayerState GetState(uint playerID)
    {
        if (!players.TryGetValue(playerID, out PlayerState? state))
        {
            players[playerID] = state = new();
        }

        return state;
    }

    private CelesteNetPlayerFrame Build(uint playerID, CelesteNetPlayerFrameInput input)
    {
        PlayerState state = GetState(playerID);
        CelesteNetPlayerFrame? appearance = state.Appearance;
        CelesteNetPlayerGraphics? graphics = state.Graphics;

        UpdateDash(state, input);
        float speedX = 0f;
        float speedY = 0f;
        UpdateSpeed(state, input, ref speedX, ref speedY);
        (CelesteNetColor hairColor, int hairLength) = HairPalette(input.Dashes, input.StarFlying);

        return new() {
            PlayerID = playerID,
            PositionX = input.PositionX,
            PositionY = input.PositionY,
            ScaleX = input.ScaleX,
            ScaleY = input.ScaleY,
            FacingLeft = input.FacingLeft,
            HairSimulateMotion = input.HairSimulateMotion,
            Color = appearance?.Color ?? CelesteNetColor.White,
            SpeedX = speedX,
            SpeedY = speedY,
            AnimationID = ResolveAnimationID(graphics, input.AnimationName),
            AnimationFrame = input.AnimationFrame,
            HairColors = ResolveHairColors(appearance, graphics, hairColor, hairLength),
            HairTexture0 = ResolveHairTexture0(appearance, graphics),
            Followers = ResolveFollowers(appearance, input.Followers),
            Holding = ResolveHolding(appearance, input.Holdable),
            Dashing = input.Dashing,
            DashWasB = input.Dashing && state.DashWasB,
            DashDirection = input.Dashing ? input.DashDirection : (byte)0,
            Dead = state.Dead,
        };
    }

    private static void UpdateDash(PlayerState state, CelesteNetPlayerFrameInput input)
    {
        if (!input.Dashing)
        {
            state.WasDashing = false;
            state.DashWasB = false;
            return;
        }

        if (!state.WasDashing)
        {
            // The particle type depends on the dash count the dash started with, exactly like
            // MiaoNet's own ghost: a first dash is an A dash, a dash after another one is a B dash.
            state.DashWasB = input.Dashes != 0;
            state.WasDashing = true;
        }
    }

    private static void UpdateSpeed(
        PlayerState state,
        CelesteNetPlayerFrameInput input,
        ref float speedX,
        ref float speedY
    )
    {
        float delta = input.DeltaSeconds > 0f ? input.DeltaSeconds : DefaultDeltaSeconds;
        if (state.HasPositionSample)
        {
            speedX = (input.PositionX - state.LastPositionX) / delta;
            speedY = (input.PositionY - state.LastPositionY) / delta;
        }

        state.HasPositionSample = true;
        state.LastPositionX = input.PositionX;
        state.LastPositionY = input.PositionY;
    }

    /// <summary>
    /// MiaoNet sends animation names; CelesteNet's frames carry an index into the sender's animation
    /// table, which arrives with the relayed graphics. An unknown name becomes -1, which the ghost
    /// treats as "keep the current animation".
    /// </summary>
    private static int ResolveAnimationID(CelesteNetPlayerGraphics? graphics, string? animationName)
    {
        if (graphics is null || string.IsNullOrEmpty(animationName))
        {
            return -1;
        }

        string[] animations = graphics.SpriteAnimations;
        for (int i = 0; i < animations.Length; i++)
        {
            if (string.Equals(animations[i], animationName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static CelesteNetColor[] ResolveHairColors(
        CelesteNetPlayerFrame? appearance,
        CelesteNetPlayerGraphics? graphics,
        CelesteNetColor paletteColor,
        int paletteLength
    )
    {
        int hairCount = graphics?.HairCount ?? 0;
        if (hairCount <= 0)
        {
            hairCount = paletteLength;
        }

        CelesteNetColor[]? template = appearance?.HairColors;
        if (template is { Length: > 0 } && template.Length == hairCount)
        {
            return template;
        }

        // The template is stale (a dash changed the hair length, or nothing arrived yet): use MiaoNet's
        // default palette for the current dash count until the next real frame corrects it.
        CelesteNetColor[] colors = new CelesteNetColor[hairCount];
        Array.Fill(colors, paletteColor);
        return colors;
    }

    private static string ResolveHairTexture0(CelesteNetPlayerFrame? appearance, CelesteNetPlayerGraphics? graphics)
    {
        if (appearance is not null && appearance.HairTexture0.Length > 0)
        {
            return appearance.HairTexture0;
        }

        if (graphics is not null && graphics.HairTexture0.Length > 0)
        {
            return graphics.HairTexture0;
        }

        return DefaultHairTexture;
    }

    private static CelesteNetPlayerFrameEntity[] ResolveFollowers(
        CelesteNetPlayerFrame? appearance,
        CelesteNetFollowerInput[]? followers
    )
    {
        if (followers is null || followers.Length == 0)
        {
            return [];
        }

        int count = Math.Min(followers.Length, MaxFollowers);
        CelesteNetPlayerFrameEntity[] template = appearance?.Followers ?? [];
        CelesteNetPlayerFrameEntity[] result = new CelesteNetPlayerFrameEntity[count];
        for (int i = 0; i < count; i++)
        {
            CelesteNetFollowerInput follower = followers[i];
            CelesteNetPlayerFrameEntity? style = i < template.Length ? template[i] : null;
            result[i] = new() {
                ScaleX = style?.ScaleX ?? 1f,
                ScaleY = style?.ScaleY ?? 1f,
                Color = style?.Color ?? CelesteNetColor.White,
                Depth = style?.Depth ?? FallbackEntityDepth,
                SpriteRate = style?.SpriteRate ?? 1f,
                HasJustify = style?.HasJustify ?? false,
                JustifyX = style?.JustifyX ?? 0f,
                JustifyY = style?.JustifyY ?? 0f,
                SpriteID = follower.SpriteID ?? "",
                AnimationID = follower.AnimationID ?? "",
                AnimationFrame = follower.AnimationFrame,
            };
        }

        return result;
    }

    private static CelesteNetPlayerFrameEntity? ResolveHolding(
        CelesteNetPlayerFrame? appearance,
        CelesteNetHoldableInput holdable
    )
    {
        string spriteID = holdable.Kind switch {
            CelesteNetHoldableKind.Theo => TheoHoldingSprite,
            CelesteNetHoldableKind.Jelly => JellyHoldingSprite,
            _ => "",
        };

        if (spriteID.Length == 0)
        {
            // CelesteNet cannot name a player-held entity or a modded one; omit the hold instead of
            // drawing the wrong sprite.
            return null;
        }

        CelesteNetPlayerFrameEntity? style = appearance?.Holding;
        if (style is not null && style.SpriteID != spriteID)
        {
            // The template still holds the previous item; its styling would fit the wrong sprite.
            style = null;
        }

        string animationID = holdable.AnimationID;
        if (string.IsNullOrEmpty(animationID))
        {
            animationID = style is not null && style.AnimationID.Length > 0 ? style.AnimationID : "idle";
        }

        bool jelly = holdable.Kind == CelesteNetHoldableKind.Jelly;
        return new() {
            OffsetX = holdable.OffsetX,
            OffsetY = holdable.OffsetY,
            ScaleX = jelly ? holdable.ScaleX : style?.ScaleX ?? 1f,
            ScaleY = jelly ? holdable.ScaleY : style?.ScaleY ?? 1f,
            Color = style?.Color ?? CelesteNetColor.White,
            Depth = style?.Depth ?? FallbackEntityDepth,
            SpriteRate = style?.SpriteRate ?? 1f,
            HasJustify = style?.HasJustify ?? false,
            JustifyX = style?.JustifyX ?? 0f,
            JustifyY = style?.JustifyY ?? 0f,
            SpriteID = spriteID,
            AnimationID = animationID,
            AnimationFrame = holdable.AnimationFrame,
        };
    }

    /// <summary>The color and segment count MiaoNet would give this dash state, per its default palette.</summary>
    private static (CelesteNetColor Color, int Length) HairPalette(int dashes, bool starFlying)
    {
        if (starFlying)
        {
            return (FeatherHairColor, 7);
        }

        return dashes switch {
            <= 0 => (Dash0HairColor, 4),
            1 => (Dash1HairColor, 4),
            _ => (Dash2HairColor, 5),
        };
    }

    /// <summary>
    /// The fields of a real frame that a synthesised frame copies instead of deriving from a MiaoNet
    /// delta. Two frames with equal projections are the same appearance, so only the first of them is
    /// worth sending as a keyframe. Per-frame fields such as the position, the animation and the dash
    /// state are not part of it: they change every tick and would make every frame a keyframe.
    /// </summary>
    private readonly struct AppearanceProjection
    {
        private readonly CelesteNetColor[] hairColors;
        private readonly string hairTexture0;
        private readonly CelesteNetColor color;
        private readonly FollowerStyle[] followers;
        private readonly bool hasHolding;
        private readonly HoldingStyle holding;

        public AppearanceProjection(CelesteNetPlayerFrame frame)
        {
            hairColors = frame.HairColors;
            hairTexture0 = frame.HairTexture0;
            color = frame.Color;

            CelesteNetPlayerFrameEntity[] frameFollowers = frame.Followers;
            followers = new FollowerStyle[frameFollowers.Length];
            for (int i = 0; i < frameFollowers.Length; i++)
            {
                CelesteNetPlayerFrameEntity follower = frameFollowers[i];
                followers[i] = new(
                    follower.ScaleX,
                    follower.ScaleY,
                    follower.Color,
                    follower.Depth,
                    follower.SpriteRate,
                    follower.HasJustify,
                    follower.JustifyX,
                    follower.JustifyY
                );
            }

            if (frame.Holding is { } held)
            {
                hasHolding = true;
                holding = new(
                    held.SpriteID,
                    held.ScaleX,
                    held.ScaleY,
                    held.Color,
                    held.Depth,
                    held.SpriteRate,
                    held.HasJustify,
                    held.JustifyX,
                    held.JustifyY
                );
            }
        }

        public bool Matches(AppearanceProjection other)
            => color == other.color
                && string.Equals(hairTexture0, other.hairTexture0, StringComparison.Ordinal)
                && hairColors.AsSpan().SequenceEqual(other.hairColors)
                && followers.AsSpan().SequenceEqual(other.followers)
                && hasHolding == other.hasHolding
                && (!hasHolding || holding.Equals(other.holding));
    }

    /// <summary>The styling of one follower, without the sprite and animation its delta carries.</summary>
    private readonly record struct FollowerStyle(
        float ScaleX,
        float ScaleY,
        CelesteNetColor Color,
        int Depth,
        float SpriteRate,
        bool HasJustify,
        float JustifyX,
        float JustifyY
    );

    /// <summary>The styling of the held entity; its sprite id decides whether the style still fits.</summary>
    private readonly record struct HoldingStyle(
        string SpriteID,
        float ScaleX,
        float ScaleY,
        CelesteNetColor Color,
        int Depth,
        float SpriteRate,
        bool HasJustify,
        float JustifyX,
        float JustifyY
    );

    private sealed class PlayerState
    {
        public CelesteNetPlayerFrame? Appearance;
        public AppearanceProjection? Projection;
        public bool AppearanceKeyframePending;
        public double? LastAppearanceKeyframeSeconds;
        public CelesteNetPlayerGraphics? Graphics;
        public bool Dead;
        public bool HasPositionSample;
        public float LastPositionX;
        public float LastPositionY;
        public bool WasDashing;
        public bool DashWasB;
    }

    private readonly record struct PendingInput(ulong EngineFrameCounter, CelesteNetPlayerFrameInput Input);
}

/// <summary>One encoded <c>playerFrame</c> payload, ready to be queued for the local real client.</summary>
public readonly record struct CelesteNetSynthesisedFrame(uint PlayerID, byte[] Payload);

/// <summary>How far CelesteNet can express MiaoNet's held-item types.</summary>
public enum CelesteNetHoldableKind : byte
{
    /// <summary>Nothing is held.</summary>
    None = 0,

    /// <summary>A Theo crystal; CelesteNet sends its plain sprite.</summary>
    Theo = 1,

    /// <summary>A jelly; CelesteNet sends its plain sprite plus the extra animation fields.</summary>
    Jelly = 2,

    /// <summary>A player or a modded holdable, which CelesteNet's own sender cannot express either.</summary>
    Unsupported = 3,
}

/// <summary>One follower of the remote player, as far as CelesteNet's frame can express it.</summary>
public readonly struct CelesteNetFollowerInput
{
    public CelesteNetFollowerInput(string? spriteID, string? animationID, int animationFrame)
    {
        SpriteID = spriteID ?? "";
        AnimationID = animationID ?? "";
        AnimationFrame = animationFrame;
    }

    /// <summary>Sprite bank id, e.g. <c>collectables/strawberry</c>. Empty means the sender had none.</summary>
    public string SpriteID { get; }

    /// <summary>Animation id inside that sprite bank.</summary>
    public string AnimationID { get; }

    public int AnimationFrame { get; }
}

/// <summary>The held entity, described the way a CelesteNet frame can carry it.</summary>
public readonly struct CelesteNetHoldableInput
{
    public CelesteNetHoldableKind Kind { get; init; }

    /// <summary>Offset from the player position, in pixels; CelesteNet stores the delta.</summary>
    public float OffsetX { get; init; }

    /// <summary>Offset from the player position, in pixels; CelesteNet stores the delta.</summary>
    public float OffsetY { get; init; }

    public string AnimationID { get; init; }

    public int AnimationFrame { get; init; }

    /// <summary>Only jelly frames carry a per-frame scale.</summary>
    public float ScaleX { get; init; }

    /// <summary>Only jelly frames carry a per-frame scale.</summary>
    public float ScaleY { get; init; }
}

/// <summary>Everything one MiaoNet player delta can say about a CelesteNet frame.</summary>
public sealed record CelesteNetPlayerFrameInput
{
    public required uint PlayerID { get; init; }

    public float PositionX { get; init; }

    public float PositionY { get; init; }

    public float ScaleX { get; init; } = 1f;

    public float ScaleY { get; init; } = 1f;

    /// <summary>Animation name from MiaoNet's delta; mapped through the relayed graphics table.</summary>
    public required string AnimationName { get; init; }

    public int AnimationFrame { get; init; }

    public bool FacingLeft { get; init; }

    public bool StarFlying { get; init; }

    public bool Dashing { get; init; }

    /// <summary>Current dash count; decides whether the running dash is an A or a B dash.</summary>
    public int Dashes { get; init; }

    /// <summary>
    /// MiaoNet's dash angle byte (<c>angle / Tau * 255</c>). The bridge rescales it to CelesteNet's
    /// byte scale, which is <c>angle / Tau * 256</c>.
    /// </summary>
    public byte DashDirection { get; init; }

    public bool HairSimulateMotion { get; init; } = true;

    /// <summary>Time since the previous frame, used to derive CelesteNet's speed field.</summary>
    public float DeltaSeconds { get; init; }

    public CelesteNetFollowerInput[] Followers { get; init; } = [];

    public CelesteNetHoldableInput Holdable { get; init; }
}

/// <summary>
/// Translates MiaoNet's held item into the CelesteNet frame field.
/// </summary>
/// <remarks>
/// The delta decides whether anything is held at all: <c>PlayerState.HoldableInfo</c> keeps the last
/// item after it is dropped (its offset is only re-sent when it changes), so reading the state alone
/// would leave a ghost holding the crystal forever.
/// </remarks>
public static class CelesteNetHoldableInputs
{
    /// <summary>
    /// The held item to put in a frame, or the empty value when the player is not holding anything.
    /// </summary>
    public static CelesteNetHoldableInput For(bool hasHoldable, HoldableInfo holdable)
        => hasHoldable ? From(holdable) : default;

    /// <summary>
    /// Maps one MiaoNet holdable. CelesteNet can only carry the two vanilla ones as a plain sprite; a
    /// player or a modded holdable is marked unsupported instead of being drawn as the wrong thing.
    /// </summary>
    public static CelesteNetHoldableInput From(HoldableInfo holdable) {
        CelesteNetHoldableKind kind = holdable.Type switch {
            HoldableType.Theo => CelesteNetHoldableKind.Theo,
            HoldableType.Jelly => CelesteNetHoldableKind.Jelly,
            HoldableType.None => CelesteNetHoldableKind.None,
            _ => CelesteNetHoldableKind.Unsupported,
        };

        if (kind is CelesteNetHoldableKind.None)
            return default;

        Vector2 offset = holdable.Offset ?? Vector2.Zero;
        CelesteNetHoldableInput input = new() {
            Kind = kind,
            OffsetX = offset.X,
            OffsetY = offset.Y,
        };

        if (kind is CelesteNetHoldableKind.Theo)
            return input;

        return input with {
            AnimationID = holdable.Animation,
            AnimationFrame = holdable.AnimationFrame,
            ScaleX = holdable.Scale.X,
            ScaleY = holdable.Scale.Y,
        };
    }
}

/// <summary>
/// Rescales MiaoNet's dash angle byte to CelesteNet's.
/// </summary>
/// <remarks>
/// MiaoNet stores <c>angle / Tau * 255</c>, CelesteNet reads <c>byte / 256</c>: the same angle needs a
/// slightly larger byte here, or every dash points a little off.
/// </remarks>
public static class CelesteNetDashDirections
{
    public static byte FromMiaoNet(byte direction)
        => (byte)Math.Clamp((direction * 256 + 127) / 255, 0, 255);
}
