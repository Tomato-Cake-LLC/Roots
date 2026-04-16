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

    // Returns the node index of the child at sibling position i (0-based).
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

    // Scratch lists for tree building. Static because BuildTree is iterative — only one
    // Setup frame runs at a time, so these lists are never accessed re-entrantly.
    //
    // s_elemsBuffer: flat storage for all frame element slices. Grows as child frames
    //                copy their elements into it; cleared at the start of each rebuild.
    // s_stack:       explicit work stack replacing the call stack.
    // s_childResults: result slots (node indices) shared across all frames in one rebuild.
    //                 Slots are allocated incrementally; written by Setup, read by Await.
    // s_pairs:        per-Setup-frame temp for (containerChildIndex, elemIdx) grouping.
    // s_runs:         per-Setup-frame temp for multi-element child runs before stack push.
    private static readonly List<(NavItem item, VisualElement ve)> s_elemsBuffer = new();
    private static readonly List<BuildFrame> s_stack = new();
    private static readonly List<int> s_childResults = new();
    private static readonly List<(int key, int elemIdx)> s_pairs = new();
    private static readonly List<(VisualElement container, int elemsStart, int elemsCount, int resultSlot)> s_runs = new();

    private struct BuildFrame {
        // When false: compute the NavNode for (container, elements slice).
        // When true:  collect child results and build the group node.
        public bool isAwait;

        // Setup fields
        public VisualElement? container;
        public int elemsStart, elemsCount;

        // Index in s_childResults where this frame writes its result.
        // -1 means this is the root frame; result goes to BuildTree's return value.
        public int resultSlot;

        // Await fields (unused in Setup frames)
        public Direction axis;
        public int childResultsStart, childCount;
    }

    private void RebuildGraph() {
        _navRootIndex = -1;
        _nodes.Clear();
        _leafLookup.Clear();
        s_elemsBuffer.Clear();

        foreach (var item in items) {
            if (!item.interactable) {
                continue;
            }
            // Defer if layout hasn't run yet — graph will rebuild on the next navigate input.
            var bounds = item.bridge.Element.worldBound;
            if (bounds.width == 0f && bounds.height == 0f) {
                return;
            }
            s_elemsBuffer.Add((item, item.bridge.Element));
        }

        var rootVe = GetVisualChild();
        if (rootVe != null && s_elemsBuffer.Count >= 1) {
            _navRootIndex = BuildTree(rootVe);
        }

        // Linear scan is simpler and cheaper than recursive traversal.
        for (int i = 0; i < _nodes.Count; i++) {
            if (_nodes[i].IsLeaf) {
                _leafLookup[_nodes[i].bridge!] = i;
            }
        }

        graphDirty = false;
    }

    // Iteratively maps the interactable elements (in s_elemsBuffer[0..]) onto the UIToolkit
    // flex tree using an explicit work stack, avoiding call-stack overflow on deep UI trees.
    //
    // Setup frames group their elements by the direct container child that contains each one,
    // then push an Await frame (which will build the group node) followed by child Setup frames
    // in reverse order (so child 0 runs first off the stack).
    // Await frames run after all their children and wire up the sibling chain + group node.
    private int BuildTree(VisualElement rootContainer) {
        s_stack.Clear();
        s_childResults.Clear();

        s_stack.Add(new BuildFrame {
            container = rootContainer,
            elemsStart = 0,
            elemsCount = s_elemsBuffer.Count,
            resultSlot = -1,
        });

        int rootResult = -1;

        while (s_stack.Count > 0) {
            var frame = s_stack[^1];
            s_stack.RemoveAt(s_stack.Count - 1);

            if (frame.isAwait) {
                ProcessAwaitFrame(frame, ref rootResult);
            } else {
                ProcessSetupFrame(frame, ref rootResult);
            }
        }

        return rootResult;
    }

    private void ProcessAwaitFrame(BuildFrame frame, ref int rootResult) {
        // Count children that produced a valid node (some runs may return -1 if empty).
        int validCount = 0;
        for (int i = 0; i < frame.childCount; i++) {
            if (s_childResults[frame.childResultsStart + i] >= 0) {
                validCount++;
            }
        }

        if (validCount == 0) {
            WriteResult(frame.resultSlot, -1, ref rootResult);
            return;
        }

        // If only one valid child survived, pass it through without wrapping in a group.
        if (validCount == 1) {
            for (int i = 0; i < frame.childCount; i++) {
                int r = s_childResults[frame.childResultsStart + i];
                if (r >= 0) {
                    WriteResult(frame.resultSlot, r, ref rootResult);
                    return;
                }
            }
        }

        // Build a group node and wire up the sibling chain.
        int groupIdx = _nodes.Count;
        _nodes.Add(new NavNode {
            axis = frame.axis,
            firstChild = -1,
            defaultChildIndex = 0,
            parentIndex = -1,
            childIndex = 0,
            nextSibling = -1,
        });

        int prevChildIdx = -1;
        int firstChildIdx = -1;
        int childPos = 0;
        for (int i = 0; i < frame.childCount; i++) {
            int childNodeIdx = s_childResults[frame.childResultsStart + i];
            if (childNodeIdx < 0) {
                continue;
            }
            if (firstChildIdx < 0) {
                firstChildIdx = childNodeIdx;
            }
            var child = _nodes[childNodeIdx];
            child.parentIndex = groupIdx;
            child.childIndex = childPos++;
            _nodes[childNodeIdx] = child;

            if (prevChildIdx >= 0) {
                var prev = _nodes[prevChildIdx];
                prev.nextSibling = childNodeIdx;
                _nodes[prevChildIdx] = prev;
            }
            prevChildIdx = childNodeIdx;
        }

        var groupNode = _nodes[groupIdx];
        groupNode.firstChild = firstChildIdx;
        // Find which child subtree contains the isDefault element, so perpendicular entry
        // lands on the correct element (e.g. entering a row of options lands on the active one).
        groupNode.defaultChildIndex = FindDefaultChildIndex(groupIdx);
        _nodes[groupIdx] = groupNode;

        WriteResult(frame.resultSlot, groupIdx, ref rootResult);
    }

    private void ProcessSetupFrame(BuildFrame frame, ref int rootResult) {
        if (frame.elemsCount == 0) {
            WriteResult(frame.resultSlot, -1, ref rootResult);
            return;
        }
        if (frame.elemsCount == 1) {
            WriteResult(frame.resultSlot, MakeLeaf(s_elemsBuffer[frame.elemsStart].item), ref rootResult);
            return;
        }

        // Group elements by the direct child of container that contains each one.
        s_pairs.Clear();
        for (int i = 0; i < frame.elemsCount; i++) {
            var ve = s_elemsBuffer[frame.elemsStart + i].ve;
            var ancestor = FindDirectChildOf(frame.container!, ve);
            if (ancestor == null) {
                continue;
            }
            int idx = frame.container!.IndexOf(ancestor);
            if (idx < 0) {
                continue;
            }
            s_pairs.Add((idx, frame.elemsStart + i));
        }

        if (s_pairs.Count == 0) {
            WriteResult(frame.resultSlot, -1, ref rootResult);
            return;
        }

        s_pairs.Sort((a, b) => a.key.CompareTo(b.key));

        // Count distinct container-child keys to detect transparent wrappers.
        int distinctKeys = 1;
        for (int i = 1; i < s_pairs.Count; i++) {
            if (s_pairs[i].key != s_pairs[i - 1].key) {
                distinctKeys++;
            }
        }

        // Transparent wrapper: all elements fall under a single child — reuse element slice,
        // push a new Setup frame for the child container (equivalent to a tail call).
        if (distinctKeys == 1) {
            s_stack.Add(new BuildFrame {
                container = frame.container!.ElementAt(s_pairs[0].key),
                elemsStart = frame.elemsStart,
                elemsCount = frame.elemsCount,
                resultSlot = frame.resultSlot,
            });
            return;
        }

        // Process each contiguous run of the same container-child key.
        // Single-element runs become leaf nodes immediately. Multi-element runs are deferred
        // as child Setup frames. Collect them in s_runs so we can push in reverse order.
        s_runs.Clear();
        int childResultsStart = s_childResults.Count;
        int runNum = 0;
        int runStart = 0;
        while (runStart < s_pairs.Count) {
            int runKey = s_pairs[runStart].key;
            int runEnd = runStart + 1;
            while (runEnd < s_pairs.Count && s_pairs[runEnd].key == runKey) {
                runEnd++;
            }

            // Reserve a result slot for this run.
            s_childResults.Add(-1);

            if (runEnd - runStart == 1) {
                // Single element: make the leaf now, write directly to the slot.
                s_childResults[childResultsStart + runNum] = MakeLeaf(s_elemsBuffer[s_pairs[runStart].elemIdx].item);
            } else {
                // Multi-element: copy this run's elements to the buffer tail for the child frame.
                int childElemsStart = s_elemsBuffer.Count;
                for (int i = runStart; i < runEnd; i++) {
                    s_elemsBuffer.Add(s_elemsBuffer[s_pairs[i].elemIdx]);
                }
                s_runs.Add((frame.container!.ElementAt(runKey), childElemsStart, runEnd - runStart, childResultsStart + runNum));
            }

            runStart = runEnd;
            runNum++;
        }

        // Push Await frame first — it runs after all children complete.
        s_stack.Add(new BuildFrame {
            isAwait = true,
            axis = GetFlexAxis(frame.container!),
            childResultsStart = childResultsStart,
            childCount = runNum,
            resultSlot = frame.resultSlot,
        });

        // Push child Setup frames in reverse order so child 0 is on top and runs first.
        for (int i = s_runs.Count - 1; i >= 0; i--) {
            var (container, elemsStart, elemsCount, resultSlot) = s_runs[i];
            s_stack.Add(new BuildFrame {
                container = container,
                elemsStart = elemsStart,
                elemsCount = elemsCount,
                resultSlot = resultSlot,
            });
        }
    }

    private static void WriteResult(int resultSlot, int nodeIdx, ref int rootResult) {
        if (resultSlot < 0) {
            rootResult = nodeIdx;
        } else {
            s_childResults[resultSlot] = nodeIdx;
        }
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
