#nullable enable

using System;
using System.Collections.Generic;
using RishUI;
using UnityEngine;
using UnityEngine.UIElements;

[RishValueType]
public struct NavigationGroupProps {
    public Element content;
    public bool visible;
}

// Wraps a set of navigable elements and provides gamepad/keyboard navigation between them.
// Only the topmost NavigationGroup in the static stack handles input, acting as a focus trap
// for the current layer (screen, modal, alert).
//
// Elements register automatically: any Div.Create(navigable: ...) inside this group causes
// Bridge to call INavigationRegistrar.RegisterNavigable when the element mounts.
public partial class NavigationGroup : RishElement<NavigationGroupProps>,
    IMountingListener, IPropsListener, INavigationRegistrar {
    public enum Direction { Vertical, Horizontal }

    public struct NavItem {
        public IBridge bridge;
        public bool interactable;
        // Focused first when this group becomes active. Falls back to first interactable.
        public bool isDefault;
        // Invokes the back action when the Cancel input fires (gamepad B / Escape).
        public bool isBackButton;
    }

    // A node in the navigation tree. Either a leaf (navigable element) or a group of siblings.
    // Built from the UIToolkit flex layout: each group's axis matches its container's flexDirection.
    // Navigation walks UP the tree to find the nearest ancestor group on the pressed axis, steps
    // to the next sibling, then resolves the entry point by descending into the target subtree.
    public sealed class NavNode {
        // Leaf only
        public IBridge? bridge;
        public bool isDefault;
        public bool isInteractable;
        public bool isBackButton;

        // Group only
        public Direction axis;
        public NavNode[]? children;
        // Index of the child subtree containing the isDefault element, else 0.
        // Used as the landing target when entering this group perpendicularly.
        public int defaultChildIndex;

        // Tree structure (set after construction)
        public NavNode? parent;
        public int childIndex;  // index within parent.children

        public bool IsLeaf => bridge != null;
    }

    // Only the topmost group handles input — others silently ignore callbacks.
    // This list is mutated at runtime and persists across play sessions (domain reloading off).
    // Elements always clean up in ElementWillUnmount, so the list stays consistent.
    private static readonly List<NavigationGroup> ActiveGroups = new();
    private bool IsTop => ActiveGroups.Count > 0 && ActiveGroups[^1] == this;

    // Instance fields: derived/cached data. NOT in a Rish state struct because they are
    // memoized state that must not participate in diffing or trigger re-renders.
    private readonly List<NavItem> items = new();
    // Navigation tree built from the UIToolkit flex layout hierarchy.
    private NavNode? _navRoot;
    // O(1) lookup from a bridge to its leaf node in the tree.
    private readonly Dictionary<IBridge, NavNode> _leafLookup = new();
    private bool graphDirty;
    private IBridge? focusedBridge;
    // The element focused before the current one, set only on explicit user navigation.
    // Used as a hint in ResolveEntry to prefer the last-visited element when entering a
    // perpendicular group (e.g. returning to row 2 after visiting row 1 lands on the
    // element you were at in row 2, not the default). Never set during initial focus so
    // registration order can't poison it.
    private IBridge? _previousBridge;

    // Repeat-navigation timing: matches InputSystemUIInputModule defaults.
    private (Direction axis, bool positive)? _lastNavDir;
    private float _navHoldStart;
    private float _lastNavTime;
    private const float NavInitialDelay = 0.4f;
    private const float NavRepeatRate = 0.12f;

    private INavigationEventProvider? _provider;

    // Captured in PropsWillChange to detect visibility transitions in PropsDidChange.
    private bool _prevVisible;

    // --- Internal accessors for NavigationGroupDebugOverlay ---

    public IReadOnlyList<NavItem> Items => items;
    public NavNode? NavRoot => _navRoot;
    public IBridge? FocusedBridge => focusedBridge;
    public static IReadOnlyList<NavigationGroup> AllActiveGroups => ActiveGroups;

    // Resolves the entry/exit point of a subtree for a given axis and direction.
    // hint: if set, perpendicular groups prefer the child subtree containing hint over defaultChildIndex.
    // Exposed for the debug overlay so it can draw accurate connection lines (hint=null).
    public static IBridge ResolveEntry(NavNode node, Direction axis, bool forward, IBridge? hint = null) {
        while (!node.IsLeaf) {
            if (node.children == null || node.children.Length == 0) {
                break;
            }
            if (node.axis == axis) {
                // Parallel: enter from start or end
                node = forward ? node.children[0] : node.children[node.children.Length - 1];
            } else {
                // Perpendicular: use hint if it lives in this subtree, else fall back to default.
                int targetIdx = node.defaultChildIndex;
                if (hint != null) {
                    for (int i = 0; i < node.children.Length; i++) {
                        if (SubtreeContains(node.children[i], hint)) {
                            targetIdx = i;
                            break;
                        }
                    }
                }
                node = node.children[targetIdx];
            }
        }
        return node.bridge!;
    }

    private static bool SubtreeContains(NavNode node, IBridge bridge) {
        if (node.IsLeaf) {
            return node.bridge == bridge;
        }
        if (node.children == null) {
            return false;
        }
        foreach (var child in node.children) {
            if (SubtreeContains(child, bridge)) {
                return true;
            }
        }
        return false;
    }

    // --- Lifecycle ---

    void IMountingListener.ElementDidMount() {
        // Subscribe to the NavigationContext provider. NavigationContext is an ancestor in the
        // Rish node tree; GetFirstAncestorOfType walks up to find it.
        // Fall back to SharedProvider directly if NavigationContext is not in the tree yet.
        var ctx = GetFirstAncestorOfType<NavigationContext>();
        _provider = ctx?.Provider ?? NavigationContext.SharedProvider;

        if (_provider != null) {
            _provider.Navigate += OnNavigate;
            _provider.NavigateCanceled += OnNavigateCanceled;
            _provider.Submit += OnSubmit;
            _provider.SubmitEnded += OnSubmitEnded;
            _provider.Cancel += OnCancel;
        } else {
            Debug.LogError("[NavigationGroup] No INavigationEventProvider found. Set NavigationContext.SharedProvider before mounting.");
        }

        // Do NOT access Props here — for pure Rish elements (RishDefinition), AddChild fires
        // ElementDidMount *before* SetProps is called. Initial visibility is handled in
        // PropsDidChange, which fires after the first SetProps (_prevVisible defaults to false,
        // so a visible:true initial prop correctly triggers PushAndFocus).
    }

    void IMountingListener.ElementWillUnmount() {
        if (_provider != null) {
            _provider.Navigate -= OnNavigate;
            _provider.NavigateCanceled -= OnNavigateCanceled;
            _provider.Submit -= OnSubmit;
            _provider.SubmitEnded -= OnSubmitEnded;
            _provider.Cancel -= OnCancel;
            _provider = null;
        }

        ActiveGroups.Remove(this);
        if (focusedBridge != null) {
            SetBridgeFocus(focusedBridge, false);
            focusedBridge = null;
        }
        _previousBridge = null;
        _navRoot = null;
        _leafLookup.Clear();
        // Domain reloading is off — Rish reuses element instances between play sessions.
        // Reset _prevVisible so the next session's first PropsDidChange detects the visible=true transition.
        _prevVisible = false;
        _lastNavDir = null;
        graphDirty = true;
    }

    void IPropsListener.PropsWillChange() {
        _prevVisible = Props.visible;
    }

    void IPropsListener.PropsDidChange() {
        if (Props.visible == _prevVisible) {
            return;
        }
        if (Props.visible) {
            PushAndFocus();
        } else {
            ActiveGroups.Remove(this);
            if (focusedBridge != null) {
                SetBridgeFocus(focusedBridge, false);
                focusedBridge = null;
            }
            _previousBridge = null;
        }
    }

    protected override Element Render() => Props.content;

    // --- INavigationRegistrar ---

    void INavigationRegistrar.RegisterNavigable(IBridge bridge, Navigable nav) {
        items.Add(new NavItem {
            bridge = bridge,
            interactable = nav.interactable,
            isDefault = nav.isDefault,
            isBackButton = nav.isBackButton,
        });
        graphDirty = true;
        // PushAndFocus fires before elements register. Focus the first eligible element on late
        // registration. If an isDefault item registers after a non-default was already focused,
        // re-run FocusDefault so the intended default wins.
        if (IsTop && (focusedBridge == null || nav.isDefault)) {
            FocusDefault();
        }
        // Eagerly build the graph when we're the active group so the debug overlay shows arrows
        // without needing a keypress, and navigation works as soon as the first key is pressed.
        // If bounds are zero (layout not done), schedule a retry on the next UIToolkit update.
        if (IsTop) {
            RebuildGraph();
            if (graphDirty) {
                GetVisualChild()?.schedule.Execute(() => {
                    if (graphDirty && IsTop) {
                        RebuildGraph();
                    }
                }).StartingIn(0);
            }
        }
    }

    void INavigationRegistrar.UnregisterNavigable(IBridge bridge) {
        int idx = items.FindIndex(i => i.bridge == bridge);
        if (idx < 0) {
            throw new InvalidOperationException("NavigationGroup.UnregisterNavigable: bridge was not registered");
        }
        if (focusedBridge == bridge) {
            SetBridgeFocus(bridge, false);
            focusedBridge = null;
        }
        items.RemoveAt(idx);
        graphDirty = true;
    }

    void INavigationRegistrar.UpdateNavigable(IBridge bridge, Navigable nav) {
        int idx = items.FindIndex(i => i.bridge == bridge);
        if (idx < 0) {
            throw new InvalidOperationException("NavigationGroup.UpdateNavigable: bridge was not registered");
        }
        var item = items[idx];
        item.interactable = nav.interactable;
        item.isDefault = nav.isDefault;
        item.isBackButton = nav.isBackButton;
        items[idx] = item;
        graphDirty = true;
    }

    void INavigationRegistrar.SetFocused(IBridge bridge, bool focused) {
        SetBridgeFocus(bridge, focused);
    }

    // --- Focus ---

    // Clears all active groups and removes focus — call before triggering a scene load so
    // the player can't navigate while the load is in progress.
    public static void DisableAll() {
        foreach (var group in ActiveGroups) {
            if (group.focusedBridge != null) {
                SetBridgeFocus(group.focusedBridge, false);
                group.focusedBridge = null;
            }
            group._previousBridge = null;
        }
        ActiveGroups.Clear();
    }

    private void PushAndFocus() {
        ActiveGroups.Add(this);
        FocusDefault();
    }

    private void FocusDefault() {
        // Prefer the explicitly designated default button, then fall back to first interactable.
        foreach (var item in items) {
            if (item.isDefault && item.interactable) {
                FocusItem(item.bridge);
                return;
            }
        }
        foreach (var item in items) {
            if (item.interactable) {
                FocusItem(item.bridge);
                return;
            }
        }
    }

    // Records history then focuses — call only from OnNavigate (user-initiated movement).
    private void NavigateTo(IBridge bridge) {
        _previousBridge = focusedBridge;
        FocusItem(bridge);
    }

    // Focuses without updating _previousBridge — call from FocusDefault and registration paths.
    private void FocusItem(IBridge bridge) {
        if (focusedBridge != null) {
            SetBridgeFocus(focusedBridge, false);
        }
        focusedBridge = bridge;
        SetBridgeFocus(bridge, true);
    }

    // Applies or removes the UIToolkit :focus pseudo-state via Plan B (direct bit flip),
    // and fires the Navigable.onFocusChanged callback for side effects.
    private static void SetBridgeFocus(IBridge bridge, bool focused) {
        var ve = bridge.Element;
        if (ve == null) {
            return;
        }
        int bits = ve.GetPseudoStates();
        ve.SetPseudoStates(focused
            ? bits | RishUI.VisualElementExtensions.FocusValue
            : bits & ~RishUI.VisualElementExtensions.FocusValue);
        bridge.GetNavigable()?.onFocusChanged?.Invoke(focused);
    }

    // --- Navigation tree ---

    private void RebuildGraph() {
        _navRoot = null;
        _leafLookup.Clear();

        var interactable = new List<(NavItem item, VisualElement ve)>();
        foreach (var item in items) {
            if (!item.interactable) {
                continue;
            }
            // Defer if layout hasn't run yet — graph will rebuild on the next navigate input.
            var bounds = item.bridge.Element.worldBound;
            if (bounds.width == 0f && bounds.height == 0f) {
                return;
            }
            interactable.Add((item, item.bridge.Element));
        }

        var rootVe = GetVisualChild();
        if (rootVe != null && interactable.Count >= 1) {
            _navRoot = BuildTree(rootVe, interactable);
        }
        if (_navRoot != null) {
            BuildLeafLookup(_navRoot);
        }

        graphDirty = false;
    }

    // Recursively maps the interactable elements onto the UIToolkit flex tree, bottom-up.
    // Each call returns a NavNode representing the subtree rooted at `container`.
    // Single-child transparent wrappers are collapsed. Multi-child containers become group nodes
    // whose axis matches the container's flex direction.
    private static NavNode? BuildTree(
        VisualElement container,
        List<(NavItem item, VisualElement ve)> elements) {
        if (elements.Count == 0) {
            return null;
        }
        if (elements.Count == 1) {
            return MakeLeaf(elements[0].item);
        }

        // Group elements by the direct child of container that contains each one.
        var groups = new SortedList<int, List<(NavItem item, VisualElement ve)>>();
        foreach (var elem in elements) {
            var ancestor = FindDirectChildOf(container, elem.ve);
            if (ancestor == null) {
                continue;
            }
            int idx = container.IndexOf(ancestor);
            if (idx < 0) {
                continue;
            }
            if (!groups.ContainsKey(idx)) {
                groups[idx] = new List<(NavItem, VisualElement)>();
            }
            groups[idx].Add(elem);
        }

        if (groups.Count == 0) {
            return null;
        }

        // Transparent wrapper: all elements under a single child — descend into it.
        if (groups.Count == 1) {
            return BuildTree(container.ElementAt(groups.Keys[0]), groups.Values[0]);
        }

        // Build a group node whose children map to each sibling group, processed bottom-up.
        var axis = GetFlexAxis(container);
        var children = new List<NavNode>(groups.Count);
        for (int i = 0; i < groups.Count; i++) {
            var group = groups.Values[i];
            NavNode child;
            if (group.Count == 1) {
                child = MakeLeaf(group[0].item);
            } else {
                var subtree = BuildTree(container.ElementAt(groups.Keys[i]), group);
                if (subtree == null) {
                    continue;
                }
                child = subtree;
            }
            child.childIndex = children.Count;
            children.Add(child);
        }

        if (children.Count == 0) {
            return null;
        }
        if (children.Count == 1) {
            return children[0];
        }

        var node = new NavNode {
            axis = axis,
            children = children.ToArray(),
            defaultChildIndex = 0,
        };
        foreach (var child in node.children) {
            child.parent = node;
        }
        // Find which child subtree contains the isDefault element, so perpendicular entry
        // lands on the correct element (e.g. entering a row of options lands on the active one).
        node.defaultChildIndex = FindDefaultChildIndex(node);
        return node;
    }

    private static NavNode MakeLeaf(NavItem item) =>
        new NavNode {
            bridge = item.bridge,
            isDefault = item.isDefault,
            isInteractable = item.interactable,
            isBackButton = item.isBackButton,
        };

    // Descends tree to find which child subtree contains the isDefault element.
    private static int FindDefaultChildIndex(NavNode node) {
        if (node.children == null) {
            return 0;
        }
        for (int i = 0; i < node.children.Length; i++) {
            if (SubtreeContainsDefault(node.children[i])) {
                return i;
            }
        }
        return 0;
    }

    private static bool SubtreeContainsDefault(NavNode node) {
        if (node.IsLeaf) {
            return node.isDefault && node.isInteractable;
        }
        if (node.children == null) {
            return false;
        }
        foreach (var child in node.children) {
            if (SubtreeContainsDefault(child)) {
                return true;
            }
        }
        return false;
    }

    // Walks up the VisualElement parent chain to find the direct child of container that
    // contains descendant, or null if descendant is not under container.
    private static VisualElement? FindDirectChildOf(VisualElement container, VisualElement descendant) {
        var current = descendant;
        while (current != null && current.parent != container) {
            current = current.parent;
        }
        return current;
    }

    private static Direction GetFlexAxis(VisualElement container) {
        var fd = container.resolvedStyle.flexDirection;
        return (fd == FlexDirection.Row || fd == FlexDirection.RowReverse)
            ? Direction.Horizontal
            : Direction.Vertical;
    }

    private void BuildLeafLookup(NavNode node) {
        if (node.IsLeaf) {
            _leafLookup[node.bridge!] = node;
            return;
        }
        if (node.children != null) {
            foreach (var child in node.children) {
                BuildLeafLookup(child);
            }
        }
    }

    // --- Input handlers ---

    private void OnNavigate(Vector2 vec) {
        if (!IsTop) {
            return;
        }
        if (graphDirty) {
            RebuildGraph();
        }

        var input = VecToDirection(vec);
        var now = Time.realtimeSinceStartup;

        if (input != _lastNavDir) {
            // Direction changed (including neutral → direction): fire immediately.
            _lastNavDir = input;
            _navHoldStart = now;
            _lastNavTime = now;
        } else if (input != null) {
            // Same direction held: wait for initial delay, then fire at repeat rate.
            if (now - _navHoldStart < NavInitialDelay) {
                return;
            }
            if (now - _lastNavTime < NavRepeatRate) {
                return;
            }
            _lastNavTime = now;
        }

        if (input == null) {
            return;
        }

        if (focusedBridge == null) {
            FocusDefault();
            return;
        }

        var (axis, positive) = input.Value;

        // Walk the navigation tree: bubble up until we find an ancestor group on the pressed axis
        // that has a sibling in the pressed direction, then descend into it.
        if (_navRoot == null || !_leafLookup.TryGetValue(focusedBridge, out var node)) {
            return;
        }

        // delta: +1 = toward higher child index (Down for V, Right for H).
        // Formula: (H axis == positive) maps (H,Right→+1) and (V,Down→+1), (H,Left→-1) and (V,Up→-1).
        int delta = (axis == Direction.Horizontal) == positive ? 1 : -1;

        while (node.parent != null) {
            var group = node.parent;
            if (group.axis == axis) {
                int nextIdx = node.childIndex + delta;
                if (nextIdx >= 0 && nextIdx < group.children!.Length) {
                    var target = ResolveEntry(group.children[nextIdx], axis, forward: delta > 0, hint: _previousBridge);
                    NavigateTo(target);
                    return;
                }
                // At this group's boundary — continue bubbling up to the parent group.
            }
            node = group;
        }
    }

    // Reset rate-limit state on release so a quick re-press always fires immediately.
    // Without this, a second tap within NavInitialDelay (0.4s) would be treated as a held
    // direction and silently dropped by the repeat-rate check.
    private void OnNavigateCanceled() {
        _lastNavDir = null;
    }

    private void OnSubmit() {
        if (!IsTop || focusedBridge == null) {
            return;
        }
        int idx = items.FindIndex(i => i.bridge == focusedBridge);
        if (idx >= 0 && items[idx].interactable) {
            focusedBridge.GetNavigable()?.action.Invoke();
        }
    }

    private void OnSubmitEnded() {
        if (!IsTop || focusedBridge == null) {
            return;
        }
        int idx = items.FindIndex(i => i.bridge == focusedBridge);
        if (idx >= 0 && items[idx].interactable) {
            focusedBridge.GetNavigable()?.holdEndAction?.Invoke();
        }
    }

    private void OnCancel() {
        if (!IsTop) {
            return;
        }
        foreach (var item in items) {
            if (item.isBackButton) {
                item.bridge.GetNavigable()?.action.Invoke();
                return;
            }
        }
    }

    // Returns (axis, positive) where positive=true means Right or Up (Input System +X / +Y).
    // UIToolkit Y increases downward, so positive Vertical (Up input) means smaller target Y.
    private static (Direction axis, bool positive)? VecToDirection(Vector2 vec) {
        if (vec.magnitude < 0.5f) {
            return null;
        }
        if (Mathf.Abs(vec.x) > Mathf.Abs(vec.y)) {
            return (Direction.Horizontal, vec.x > 0f);
        }
        return (Direction.Vertical, vec.y > 0f);
    }
}
