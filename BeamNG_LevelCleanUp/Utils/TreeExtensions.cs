namespace BeamNG_LevelCleanUp.Utils;

public static class TreeExtensions
{
    /// <summary> Generic interface for tree node structure </summary>
    /// <typeparam name="T"></typeparam>
    public interface ITree<T>
    {
        T Data { get; }
        ITree<T> Parent { get; }
        HashSet<ITree<T>> Children { get; }
        bool HasPartialChildSelection { get; }
        bool IsRoot { get; }
        bool IsLeaf { get; }
        bool HasChildren { get; }
        bool IsSelected { get; set; }
        bool IsExpanded { get; set; }
        int Level { get; }
    }

    /// <summary> Flatten tree to plain list of nodes </summary>
    public static IEnumerable<TNode> Flatten<TNode>(this IEnumerable<TNode> nodes, Func<TNode, IEnumerable<TNode>> childrenSelector)
    {
        if (nodes == null) throw new ArgumentNullException(nameof(nodes));
        return nodes.SelectMany(c => childrenSelector(c).Flatten(childrenSelector)).Concat(nodes);
    }

    /// <summary> Converts given list to tree. </summary>
    /// <typeparam name="T">Custom data type to associate with tree node.</typeparam>
    /// <param name="items">The collection items.</param>
    /// <param name="parentSelector">Expression to select parent.</param>
    /// <param name="selectedItems">The collection items to preselect.</param>
    public static ITree<T> ToTree<T>(this IList<T> items, Func<T, T, bool> parentSelector, ICollection<T> selectedItems)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        var lookup = items.ToLookup(item => items.FirstOrDefault(parent => parentSelector(parent, item)),
            child => child);
        return Tree<T>.FromLookup(lookup, selectedItems);
    }

    /// <summary> Internal implementation of <see cref="ITree{T}" /></summary>
    /// <typeparam name="T">Custom data type to associate with tree node.</typeparam>
    internal class Tree<T> : ITree<T>
    {
        public T Data { get; }
        public ITree<T> Parent { get; private set; }
        public HashSet<ITree<T>> Children { get; }
        public bool HasPartialChildSelection
            => Children.Count > 0 && Children.Count(x => x.IsSelected) > 0
                && Children.Count(x => x.IsSelected) < Children.Count();
        public bool IsRoot => Parent == null;
        public bool IsLeaf => Children.Count == 0;
        public bool HasChildren => Children.Count > 0;
        public bool IsSelected { get; set; }
        public bool IsExpanded { get; set; } = true;
        public int Level => IsRoot ? 0 : Parent.Level + 1;
        private Tree(T data)
        {
            Children = new HashSet<ITree<T>>();
            Data = data;
        }
        public static Tree<T> FromLookup(ILookup<T, T> lookup, ICollection<T> selectedItems)
        {
            var rootData = lookup.Count == 1 ? lookup.First().Key : default(T);
            var root = new Tree<T>(rootData);
            root.IsSelected = Selected(rootData, selectedItems);
            root.LoadChildren(lookup, selectedItems);
            return root;
        }

        private void LoadChildren(ILookup<T, T> lookup, ICollection<T> selectedItems)
        {
            foreach (var data in lookup[Data])
            {
                var child = new Tree<T>(data) { Parent = this };
                child.IsSelected = Selected(data, selectedItems);
                Children.Add(child);
                child.LoadChildren(lookup, selectedItems);
            }
        }

        private static bool Selected(T? data, ICollection<T> selectedItems)
        {
            var retVal = data != null && selectedItems.Any(x => x.Equals(data));
            return retVal;
        }
    }
}

