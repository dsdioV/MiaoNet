using System.Reflection;

namespace Celeste.Mod.MiaoNet;

/// <summary>
/// One connectable MiaoNet side, that is, the module behind one <c>everest.yaml</c>
/// meta of the merged Verðandi package.
///
/// The package deliberately ships two assemblies (each one carries its own packet
/// registry, see make-verdandi-multizip.py), so the other side's
/// <c>MiaoNetContext</c> is a different runtime type from ours and can only be
/// reached through <see cref="Everest.Modules"/> and reflection.
/// </summary>
internal sealed class MiaoNetServerSide
{
    private readonly EverestModule module;
    private readonly PropertyInfo contextProperty;
    private readonly PropertyInfo hasConnectionProperty;
    private readonly PropertyInfo targetServerProperty;
    private readonly MethodInfo connectMethod;
    private readonly MethodInfo disconnectMethod;

    private object? context;

    private MiaoNetServerSide(EverestModule module, PropertyInfo contextProperty)
    {
        this.module = module;
        this.contextProperty = contextProperty;

        Type contextType = contextProperty.PropertyType;
        hasConnectionProperty = contextType.GetProperty("HasConnection")!;
        targetServerProperty = contextType.GetProperty("TargetServer")!;
        connectMethod = contextType.GetMethod("Connect", Type.EmptyTypes)!;
        disconnectMethod = contextType.GetMethod("Disconnect", Type.EmptyTypes)!;
    }

    /// <summary>The owning meta's name, used as the remembered identity.</summary>
    public string Name => module.Metadata.Name;

    /// <summary>Endpoint this side connects to, for display.</summary>
    public string Server
    {
        get
        {
            try
            {
                return targetServerProperty.GetValue(Context) as string ?? Name;
            }
            catch (Exception e)
            {
                Logger.LogDetailed(e);
                return Name;
            }
        }
    }

    public bool HasConnection
    {
        get
        {
            try
            {
                return hasConnectionProperty.GetValue(Context) is true;
            }
            catch (Exception e)
            {
                Logger.LogDetailed(e);
                return false;
            }
        }
    }

    private object Context => context ??= contextProperty.GetValue(module)
        ?? throw new InvalidOperationException($"{Name} has no MiaoNet context.");

    public void Connect() => Invoke(connectMethod);

    public void Disconnect() => Invoke(disconnectMethod);

    private void Invoke(MethodInfo method)
    {
        try
        {
            method.Invoke(Context, null);
        }
        catch (Exception e)
        {
            Logger.Error(LT.MiaoNet, $"Failed to call {method.Name} on {Name}.");
            Logger.LogDetailed(e);
        }
    }

    /// <summary>
    /// Every loaded module exposing a MiaoNet context, our own side first.
    /// Duck typed on the context's public surface, so a module that merely
    /// happens to have a <c>MiaoNetContext</c> property is ignored.
    /// </summary>
    public static List<MiaoNetServerSide> Discover()
    {
        List<MiaoNetServerSide> sides = [];
        MiaoNetServerSide? own = null;
        foreach (EverestModule module in Everest.Modules)
        {
            PropertyInfo? contextProperty = module.GetType().GetProperty(
                "MiaoNetContext", BindingFlags.Public | BindingFlags.Instance);
            if (contextProperty is null)
                continue;

            Type contextType = contextProperty.PropertyType;
            if (contextType.GetProperty("HasConnection") is null
                || contextType.GetProperty("TargetServer") is null
                || contextType.GetMethod("Connect", Type.EmptyTypes) is null
                || contextType.GetMethod("Disconnect", Type.EmptyTypes) is null)
                continue;

            MiaoNetServerSide side = new(module, contextProperty);
            if (module == MiaoNetModule.Instance)
                own = side;
            else
                sides.Add(side);
        }

        if (own is not null)
            sides.Insert(0, own);

        return sides;
    }

    /// <summary>
    /// The remembered side when it is still loaded, otherwise our own.
    /// <paramref name="sides"/> must not be empty.
    /// </summary>
    public static MiaoNetServerSide ResolvePreferred(List<MiaoNetServerSide> sides, string? remembered)
        => sides.FirstOrDefault(s => s.Name == remembered) ?? sides[0];
}

/// <summary>
/// Marks the pause menu entry owned by this module. The other side of the merged
/// package inserts a plain <see cref="TextMenu.Button"/> with the very same label,
/// which is what <see cref="MiaoNetModule"/> de-duplicates away.
/// </summary>
internal sealed class MiaoNetPauseMenuButton(string label) : TextMenu.Button(label);

/// <summary>
/// The single pause menu entry of the merged Verðandi package: pick a side (that
/// is, which server to talk to) and connect to or disconnect from it.
///
/// Every other knob stays in the module's own options, reachable through
/// Everest's Mod Options list, see <see cref="MenuMiaoNetOptions"/>.
/// </summary>
internal static class MiaoNetServerPicker
{
    public static void Build(TextMenu menu)
    {
        List<MiaoNetServerSide> sides = MiaoNetServerSide.Discover();
        if (sides.Count == 0)
        {
            Logger.Error(LT.MiaoNet, "No MiaoNet side is loaded, cannot build the server picker.");
            return;
        }

        // a live connection wins over the remembered choice, so the panel never
        // claims the player is somewhere they are not
        MiaoNetServerSide? live = sides.FirstOrDefault(side => side.HasConnection);
        MiaoNetServerSide selected = live ?? MiaoNetServerSide.ResolvePreferred(
            sides, MiaoNetModule.Settings.LastSelectedServer);

        menu.Add(new TextMenu.Header(Dialog.Get("miaonet_options_title")));

        if (sides.Count > 1)
        {
            TextMenu.Option<string> selector = new(Dialog.Get("miaonet_menu_server"));
            foreach (MiaoNetServerSide side in sides)
                selector.Add(side.Server, side.Name, side == selected);
            selector.Change(name =>
            {
                MiaoNetServerSide? next = sides.FirstOrDefault(s => s.Name == name);
                if (next is null || next == selected)
                    return;
                selected = next;
                MiaoNetModule.Settings.LastSelectedServer = next.Name;
            });
            menu.Add(selector);
        }

        TextMenu.Button connect = new(ConnectLabel(selected));
        connect.Pressed(() =>
        {
            if (selected.HasConnection)
            {
                selected.Disconnect();
                return;
            }

            // only one side may hold a session at a time: otherwise both would
            // draw their own avatars and HUD for the same player
            foreach (MiaoNetServerSide other in sides)
            {
                if (other != selected && other.HasConnection)
                {
                    Logger.Info(LT.MiaoNet, $"Disconnecting {other.Server} before connecting {selected.Server}.");
                    other.Disconnect();
                }
            }
            selected.Connect();
        });
        // the label always describes the side selected right now, which may have
        // changed after the selector moved or after an async connect landed
        connect.OnUpdate = () =>
        {
            string label = ConnectLabel(selected);
            if (connect.Label == label)
                return;
            connect.Label = label;
            connect.Container?.RecalculateSize();
        };
        menu.Add(connect);
    }

    private static string ConnectLabel(MiaoNetServerSide side)
        => Dialog.Get(side.HasConnection ? "miaonet_menu_disconnect" : "miaonet_menu_connect");
}
