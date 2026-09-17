using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WindowWorks.App.UI
{
    /// <summary>
    /// One node in the lazy-loaded element tree shown by <see cref="PickerElementTreeWindow"/>
    /// (docs/REPARENT_FEATURE_PLAN.md §6.7, Piece B). Deliberately UI-Automation-agnostic — this
    /// class lives in WindowWorks.App.UI, which does not (and per the existing project-reference
    /// direction, must not) reference WindowWorks.App or <c>System.Windows.Automation</c>
    /// directly. The actual UIA-backed child enumeration is supplied by the caller (an
    /// orchestrator in WindowWorks.App) as a <see cref="_childrenLoader"/> delegate, invoked only
    /// once, lazily, the first time a node is actually expanded in the tree — never eagerly for
    /// the whole subtree, since real pages can have very large/deep DOM trees.
    /// </summary>
    public sealed class ElementTreeNodeItem
    {
        private readonly Func<ElementTreeNodeItem, IReadOnlyList<ElementTreeNodeItem>>? _childrenLoader;
        private bool _childrenLoaded;

        /// <summary>Short label shown in the tree row (already elided if long; see <see cref="FullLabel"/>).</summary>
        public string Label { get; }

        /// <summary>Untruncated label shown as a tooltip, mirroring <see cref="PickerAncestorBoxItem.FullLabel"/>.</summary>
        public string FullLabel { get; }

        /// <summary>
        /// Clipped screen-pixel rect this node corresponds to, used to re-highlight the
        /// on-screen element via <see cref="PickerHighlightWindow.ShowAroundScreenRect"/> when
        /// the node is selected. May be <c>default</c> (all zero) for a node with no resolvable
        /// bounds, in which case the caller should skip re-highlighting.
        /// </summary>
        public (int Left, int Top, int Right, int Bottom) ScreenRect { get; }

        public bool HasScreenRect { get; }

        /// <summary>
        /// True when this node is known (without needing to load children yet) to have at least
        /// one child — controls whether WPF renders an expander arrow for it at all. Determined
        /// cheaply by the caller (e.g. UIA's own child-count/first-child check) rather than by
        /// actually loading the children up front.
        /// </summary>
        public bool HasChildren { get; }

        /// <summary>
        /// Opaque back-reference to whatever the caller needs to identify this node again later
        /// (e.g. a captured UIA element/runtime ID) — round-tripped back to the caller by
        /// <see cref="PickerElementTreeWindow"/>'s selection/confirm events untouched. This class
        /// itself never interprets it.
        /// </summary>
        public object? Tag { get; }

        /// <summary>
        /// Lazily-populated children collection bound directly by the tree view's
        /// <c>HierarchicalDataTemplate</c>. Starts empty; <see cref="EnsureChildrenLoaded"/>
        /// populates it exactly once, the first time the node is expanded.
        /// </summary>
        public ObservableCollection<ElementTreeNodeItem> Children { get; } = new();

        public ElementTreeNodeItem(
            string label,
            (int Left, int Top, int Right, int Bottom)? screenRect,
            bool hasChildren,
            Func<ElementTreeNodeItem, IReadOnlyList<ElementTreeNodeItem>>? childrenLoader,
            object? tag = null,
            string? fullLabel = null)
        {
            Label = label;
            FullLabel = fullLabel ?? label;
            HasScreenRect = screenRect.HasValue;
            ScreenRect = screenRect ?? default;
            HasChildren = hasChildren;
            _childrenLoader = childrenLoader;
            Tag = tag;

            if (hasChildren)
            {
                // Placeholder so WPF renders an expander arrow before real children are known —
                // replaced by EnsureChildrenLoaded() the first time this node is actually expanded.
                Children.Add(PlaceholderNode);
            }
        }

        /// <summary>
        /// Sentinel placeholder item shown as a node's only child until real expansion happens.
        /// Never itself displayed as more than a temporary loading row; identified by reference
        /// equality so <see cref="EnsureChildrenLoaded"/> can recognize and remove it.
        /// </summary>
        public static readonly ElementTreeNodeItem PlaceholderNode =
            new("Loading...", screenRect: null, hasChildren: false, childrenLoader: null);

        /// <summary>
        /// Loads real children via the supplied loader the first time this is called, replacing
        /// the placeholder. Safe to call repeatedly — a no-op after the first successful load.
        /// Best-effort: if the loader throws (e.g. the underlying UIA element went away), leaves
        /// the node with no children rather than propagating, since a single stale/gone node
        /// should not break navigation of the rest of the tree.
        /// </summary>
        public void EnsureChildrenLoaded()
        {
            if (_childrenLoaded || _childrenLoader is null)
            {
                return;
            }
            _childrenLoaded = true;

            Children.Clear();
            IReadOnlyList<ElementTreeNodeItem> loaded;
            try
            {
                loaded = _childrenLoader(this);
            }
            catch
            {
                return;
            }

            foreach (var child in loaded)
            {
                Children.Add(child);
            }
        }

        /// <summary>
        /// Recursively loads this node's entire descendant subtree via repeated
        /// <see cref="EnsureChildrenLoaded"/> calls, for the "expand all" gesture (docs/
        /// REPARENT_FEATURE_PLAN.md §6.7). Deliberately opt-in and only invoked explicitly by the
        /// caller (never automatically) since it defeats the lazy-loading design's whole purpose
        /// of avoiding eager full-subtree UIA walks on large/deep pages — the caller accepts that
        /// cost when it asks for this.
        /// <paramref name="remainingNodeBudget"/> bounds the TOTAL number of nodes loaded across
        /// the whole call tree (not just per-branch) — real pages can have tens of thousands of
        /// DOM elements, and walking all of them via UIA synchronously can take a very long time
        /// or appear to hang; the budget stops the walk once it's spent regardless of how deep or
        /// wide the remaining subtree is, leaving any unvisited node as still lazily-expandable
        /// (its placeholder is untouched) rather than blocking indefinitely.
        /// Charges the budget by the actual number of children materialized by
        /// <see cref="EnsureChildrenLoaded"/> (not a flat 1 per call) — a single wide node can
        /// still enumerate up to its loader's own per-level cap (e.g. 500 siblings) in one
        /// underlying UIA walk regardless of budget, since a lazily-loaded node's children are
        /// always loaded all-or-nothing, but charging the real count here means that cost is
        /// reflected in the budget and stops recursion into further siblings/levels promptly
        /// afterward, rather than only throttling depth while breadth goes uncounted.
        /// <paramref name="maxDepth"/> is a secondary guard against runaway recursion (e.g. an
        /// unexpectedly cyclic or pathologically deep native tree).
        /// </summary>
        public void EnsureSubtreeLoaded(ref int remainingNodeBudget, int maxDepth = 200)
        {
            if (maxDepth <= 0 || remainingNodeBudget <= 0)
            {
                return;
            }

            EnsureChildrenLoaded();
            int loadedChildCount = 0;
            foreach (var child in Children)
            {
                if (!ReferenceEquals(child, PlaceholderNode))
                {
                    loadedChildCount++;
                }
            }
            remainingNodeBudget -= Math.Max(1, loadedChildCount);

            foreach (var child in Children)
            {
                if (remainingNodeBudget <= 0)
                {
                    break;
                }
                if (!ReferenceEquals(child, PlaceholderNode))
                {
                    child.EnsureSubtreeLoaded(ref remainingNodeBudget, maxDepth - 1);
                }
            }
        }
    }
}
