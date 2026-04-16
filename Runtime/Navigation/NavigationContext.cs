#nullable enable

using RishUI;

// Props uses non-nullable Element which is safe with [RishValueType] — MemCmp on different
// pointers short-circuits before any recursive comparer is called (see rish-gotchas).
[RishValueType]
public struct NavigationContextProps {
    public Element content;
}

// Root-level wrapper providing INavigationEventProvider to descendant NavigationGroups.
// Set SharedProvider before mounting the UI tree; NavigationGroups discover this element
// via GetFirstAncestorOfType<NavigationContext>() and subscribe to Provider's events.
public partial class NavigationContext : RishElement<NavigationContextProps> {
    // Set by the app (e.g. StartMenuApp) before the Rish tree is built.
    public static INavigationEventProvider? SharedProvider;

    // NavigationGroups read this when they mount.
    public INavigationEventProvider? Provider => SharedProvider;

    protected override Element Render() => Props.content;
}
