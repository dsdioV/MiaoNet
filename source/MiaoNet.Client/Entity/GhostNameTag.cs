using MiaoNet.Shared;

namespace Celeste.Mod.MiaoNet;

public sealed class GhostNameTag : MiaoNetEntity
{
    public bool IsOnSelf { get; }

    private readonly Func<Entity?>? targetResolver;
    private readonly int onlinePlayerId;
    private Entity? entity;

    /// <summary>
    /// The entity this tag follows, or null while a lazily resolved target does not exist yet.
    /// A tag created for a CelesteNet ghost resolves its target again on every frame, because the
    /// ghost may only appear after the tag was created.
    /// </summary>
    public Entity? Entity
    {
        get => entity ?? targetResolver?.Invoke();
        set => entity = value;
    }

    public string Text { get; }

    /// <summary>
    /// The name as the player sees it right now: <see cref="Text"/> plus anything a mod added to it.
    /// Resolved on every frame rather than at construction, because a mod can change it at any time
    /// and the tag outlives that.
    /// </summary>
    private string CurrentText
        => RemoteNameTagSuffix.Get(IsOnSelf ? RemoteNameTagSuffix.SelfKey : onlinePlayerId) is { } suffix
            ? Text + suffix
            : Text;

    public Color Color { get; }

    private GhostNameTag(Entity? entity, Func<Entity?>? targetResolver, string text, Color color, int onlinePlayerId)
    {
        Tag = MiaoNetTag.Hud;
        this.entity = entity;
        this.targetResolver = targetResolver;
        this.onlinePlayerId = onlinePlayerId;
        Text = text;
        Color = color;
    }

    private GhostNameTag(Entity? entity, Func<Entity?>? targetResolver, OnlinePlayer onlinePlayer, bool avatar)
        : this(
            entity,
            targetResolver,
            onlinePlayer.GetDisplayName(true, avatar),
            onlinePlayer.Info.Color,
            onlinePlayer.ID
        )
    {
    }

    public GhostNameTag(Player player, OnlinePlayer onlinePlayer, bool avatar)
        : this(player, null, onlinePlayer, avatar)
    {
        IsOnSelf = true;
    }

    public GhostNameTag(MiaoNetGhost ghost, OnlinePlayer onlinePlayer, bool avatar)
        : this(ghost, null, onlinePlayer, avatar)
    {
        IsOnSelf = false;
    }

    /// <summary>
    /// Creates a tag that resolves the entity rendering a remote player on every frame. The full player
    /// pipeline uses it because CelesteNet's ghost can appear after the tag was created and may be gone
    /// while the player still exists; the tag draws nothing until a target is available.
    /// </summary>
    public GhostNameTag(OnlinePlayer onlinePlayer, Func<Entity?> targetResolver, bool avatar)
        : this(null, targetResolver, onlinePlayer, avatar)
    {
        IsOnSelf = false;
    }

    public override void Render()
    {
        base.Render();

        Entity? target = Entity;
        if (target is null)
            return;

        const float BaseScale = 1f / 2f;
        const float Margin = 8f;
        const float FadeRadius = 128f;

        float scale = BaseScale * SceneAs<Level>().Zoom;

        var f = ExtendedVariantInterop.GetCurrentVariantValue;
        bool upsideDown = f is not null && (bool)f.Invoke("UpsideDown");

        Vector2 justify = new Vector2(1f / 2f, !upsideDown ? 1f : 0f);

        Vector2 worldPosition = target.Position;
        worldPosition.Y -= 16f;

        Vector2 position = SceneAs<Level>().WorldToScreen(worldPosition);
        if (upsideDown)
            position.Y = Celeste.TargetHeight - position.Y;
        string text = CurrentText;

        Vector2 clampedPosition = ScreenClamper.ClampIntoScreen(
            position,
            MiaoNetFont.Measure(text) * scale,
            justify,
            Margin
        );

        var settings = MiaoNetModule.Settings;
        float alpha = IsOnSelf
            ? settings.SelfNameOpacityValue
            : position == clampedPosition
                ? settings.PlayerNameOpacityValue
                : Calc.LerpClamp(
                    settings.PlayerNameOpacityValue,
                    settings.OffScreenPlayerNameOpacityValue,
                    Vector2.DistanceSquared(position, clampedPosition) / (FadeRadius * FadeRadius)
                );
        MiaoNetFont.DrawOutline(
            text,
            clampedPosition,
            justify,
            Vector2.One * scale,
            Color * alpha
        );
    }
}
