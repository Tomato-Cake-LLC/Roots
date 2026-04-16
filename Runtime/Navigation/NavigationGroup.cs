#nullable enable

using System;
using System.Collections.Generic;
using RishUI;
using Roots;
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
    //
    // All nodes live in NavigationGroup._nodes (flat arena). Children and siblings are linked
    // by index — no heap allocations per node or per rebuild.
    public struct NavNode {
        // Leaf only
        public IBridge? bridge;
        public bool isDefault;
        public bool isInteractable;
        public bool isBackButton;

        // Group only
        public Direction axis;
        // Index of the first child in _nodes, -1 if none.
        public int firstChild;
        // Index of the child subtree containing the isDefault element, else 0.
        // Used as the landing target when entering this group perpendicularly.
        public int defaultChildIndex;

        // Tree structure (set during BuildTree)
        public int parentIndex;   // index into _nodes, -1 = no parent
        public int childIndex;    // position among siblings (0-based)
        public int nextSibling;   // index into _nodes, -1 = last sibling

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
    // Navigation tree: all nodes as structs in a flat arena, reused across rebuilds.
    private readonly List<NavNode> _nodes = new();
    // Index of the root node in _nodes, or -1 when the tree is empty.
    private int _navRootIndex = -1;
    // O(1) lookup from a bridge to its leaf node index in _nodes.
    private readonly Dictionary<IBridge, int> _leafLookup = new();
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
    public IReadOnlyList<NavNode> Nodes => _nodes;
    public int NavRootIndex => _navRootIndex;
    public IBridge? FocusedBridge => focusedBridge;
    public static IReadOnlyList<NavigationGroup> AllActiveGroups => ActiveGroups;

    // Resolves the entry/exit point of a subtree for a given axis and direction.
    // hint: if set, perpendicular groups prefer the child subtree containing hint over defaultChildIndex.
    // Exposed for the debug overlay so it can draw accurate connection lines (hint=null).
    public IBridge ResolveEntry(int nodeIdx, Direction axis, bool forward, IBridge? hint = null) {
        while (true) {
            var node = _nodes[nodeIdx];
            if (node.IsLeaf) {
                return node.bridge!;
            }
            if (node.firstChild < 0) {
                break;
            }
            if (node.axis == axis) {
                // Parallel: enter from start or end
                nodeIdx = forward ? node.firstChild : LastChild(nodeIdx);
            } else {
                // Perpendicular: use hint if it lives in this subtree, else fall back to default.
                int targetIdx = node.defaultChildIndex;
                if (hint != null) {
                    int cur = node.firstChild;
                    for (int i = 0; cur >= 0; i++, cur = _nodes[cur].nextSibling) {
                        if (SubtreeContains(cur, hint)) {
                            targetIdx = i;
                            break;
                        }
                    }
                }
                nodeIdx = ChildAt(nodeIdx, targetIdx);
            }
        }
        return _nodes[nodeIdx].bridge!;
    }

    // Returns the index of the last child of the node at groupIdx.
    private int LastChild(int groupIdx) {
        int cur = _nodes[groupIdx].firstChild;
        while (_nodes[cur].nextSibling >= 0) {
            cur = _nodes[cur].nextSibling;
        }
        return cur;
    }

    // Returns the index of the child at sibling position i (0-based).
    private int ChildAt(int groupIdx, int i) {
        int cur = _nodes[groupIdx].firstChild;
        for (int j = 0; j < i && cur >= 0; j++) {
            cur = _nodes[cur].nextSibling;
        }
        return cur;
    }

    private bool SubtreeContains(int nodeIdx, IBridge bridge) {
        var node = _nodes[nodeIdx];
        if (node.IsLeaf) {
            return node.bridge == bridge;
        }
        for (int cur = node.firstChild; cur >= 0; cur = _nodes[cur].nextSibling) {
            if (SubtreeContains(cur, bridge)) {
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
        _navRootIndex = -1;
        _nodes.Clear();
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
        _navRootIndex = -1;
        _nodes.Clear();
        _leafLookup.Clear();

        using var interactable = ListPool<(NavItem item, VisualElement ve)>.Take();
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
            _navRootIndex = BuildTree(rootVe, interactable.list);
        }

        // Linear scan is simpler and cheaper than recursive traversal.
        for (int i = 0; i < _nodes.Count; i++) {
            if (_nodes[i].IsLeaf) {
                _leafLookup[_nodes[i].bridge!] = i;
            }
        }

        graphDirty = false;
    }

    // Recursively maps the interactable elements onto the UIToolkit flex tree, bottom-up.
    // Returns the index of the resulting node in _nodes, or -1 if no node was produced.
    // Single-child transparent wrappers are collapsed. Multi-child containers become group nodes
    // whose axis matches the container's flex direction.
    private int BuildTree(VisualElement container, List<(NavItem item, VisualElement ve)> elements) {
        if (elements.Count == 0) {
            return -1;
        }
        if (elements.Count == 1) {
            return MakeLeaf(elements[0].item);
        }

        // Group elements by the direct child of container that contains each one.
        // Flat list of (containerChildIndex, elemIdx) pairs, sorted by key, then processed
        // as contiguous runs — avoids SortedList<int, List<...>> allocations.
        using var pairs = ListPool<(int key, int elemIdx)>.Take();
        for (int i = 0; i < elements.Count; i++) {
            var ancestor = FindDirectChildOf(container, elements[i].ve);
            if (ancestor == null) {
                continue;
            }
            int idx = container.IndexOf(ancestor);
            if (idx < 0) {
                continue;
            }
            pairs.Add((idx, i));
        }

        if (pairs.Count == 0) {
            return -1;
        }

        pairs.list.Sort((a, b) => a.key.CompareTo(b.key));

        // Count distinct container-child keys to detect transparent wrappers.
        int distinctKeys = 1;
        for (int i = 1; i < pairs.Count; i++) {
            if (pairs[i].key != pairs[i - 1].key) {
                distinctKeys++;
            }
        }

        // Transparent wrapper: all elements fall under a single child — descend into it.
        if (distinctKeys == 1) {
            return BuildTree(container.ElementAt(pairs[0].key), elements);
        }

        // Build one child node per contiguous run of the same container-child key.
        // Link them as a sibling chain: firstChild → nextSibling → ... → -1.
        using var childNodeIndices = ListPool<int>.Take();
        int runStart = 0;
        while (runStart < pairs.Count) {
            int runKey = pairs[runStart].key;
            int runEnd = runStart + 1;
            while (runEnd < pairs.Count && pairs[runEnd].key == runKey) {
                runEnd++;
            }

            int childIdx;
            if (runEnd - runStart == 1) {
                childIdx = MakeLeaf(elements[pairs[runStart].elemIdx].item);
            } else {
                using var subElems = ListPool<(NavItem item, VisualElement ve)>.Take();
                for (int i = runStart; i < runEnd; i++) {
                    subElems.Add(elements[pairs[i].elemIdx]);
                }
                childIdx = BuildTree(container.ElementAt(runKey), subElems.list);
            }

            if (childIdx >= 0) {
                childNodeIndices.Add(childIdx);
            }
            runStart = runEnd;
        }

        if (childNodeIndices.Count == 0) {
            return -1;
        }
        if (childNodeIndices.Count == 1) {
            return childNodeIndices[0];
        }

        // groupIdx is known before Add() since it will be appended at the current end.
        int groupIdx = _nodes.Count;

        // Wire up sibling chain and set parent/childIndex on each child.
        for (int i = 0; i < childNodeIndices.Count; i++) {
            int childIdx = childNodeIndices[i];
            var child = _nodes[childIdx];
            child.parentIndex = groupIdx;
            child.childIndex = i;
            child.nextSibling = i + 1 < childNodeIndices.Count ? childNodeIndices[i + 1] : -1;
            _nodes[childIdx] = child;
        }

        _nodes.Add(new NavNode {
            axis = GetFlexAxis(container),
            firstChild = childNodeIndices[0],
            defaultChildIndex = 0,
            parentIndex = -1,
            childIndex = 0,
            nextSibling = -1,
        });

        // Find which child subtree contains the isDefault element, so perpendicular entry
        // lands on the correct element (e.g. entering a row of options lands on the active one).
        var groupNode = _nodes[groupIdx];
        groupNode.defaultChildIndex = FindDefaultChildIndex(groupIdx);
        _nodes[groupIdx] = groupNode;

        return groupIdx;
    }

    private int MakeLeaf(NavItem item) {
        int idx = _nodes.Count;
        _nodes.Add(new NavNode {
            bridge = item.bridge,
            isDefault = item.isDefault,
            isInteractable = item.interactable,
            isBackButton = item.isBackButton,
            firstChild = -1,
            parentIndex = -1,
            childIndex = 0,
            nextSibling = -1,
        });
        return idx;
    }

    // Descends tree to find which child subtree contains the isDefault element.
    private int FindDefaultChildIndex(int groupIdx) {
        int cur = _nodes[groupIdx].firstChild;
        for (int i = 0; cur >= 0; i++, cur = _nodes[cur].nextSibling) {
            if (SubtreeContainsDefault(cur)) {
                return i;
            }
        }
        return 0;
    }

    private bool SubtreeContainsDefault(int nodeIdx) {
        var node = _nodes[nodeIdx];
        if (node.IsLeaf) {
            return node.isDefault && node.isInteractable;
        }
        for (int cur = node.firstChild; cur >= 0; cur = _nodes[cur].nextSibling) {
            if (SubtreeContainsDefault(cur)) {
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
        if (_navRootIndex < 0 || !_leafLookup.TryGetValue(focusedBridge, out var curIdx)) {
            return;
        }

        // delta: +1 = toward higher child index (Down for V, Right for H).
        // Formula: (H axis == positive) maps (H,Right→+1) and (V,Down→+1), (H,Left→-1) and (V,Up→-1).
        int delta = (axis == Direction.Horizontal) == positive ? 1 : -1;

        while (true) {
            var cur = _nodes[curIdx];
            if (cur.parentIndex < 0) {
                break;
            }
            var group = _nodes[cur.parentIndex];
            if (group.axis == axis) {
                int targetChildIdx = cur.childIndex + delta;
                if (targetChildIdx >= 0) {
                    int targetNodeIdx = ChildAt(cur.parentIndex, targetChildIdx);
                    if (targetNodeIdx >= 0) {
                        var target = ResolveEntry(targetNodeIdx, axis, forward: delta > 0, hint: _previousBridge);
                        NavigateTo(target);
                        return;
                    }
                }
                // At this group's boundary — continue bubbling up to the parent group.
            }
            curIdx = cur.parentIndex;
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
