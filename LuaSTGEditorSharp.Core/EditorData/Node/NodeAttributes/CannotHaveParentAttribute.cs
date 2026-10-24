using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LuaSTGEditorSharp.EditorData.Node.NodeAttributes
{
    /// <summary>
    /// Identify a <see cref="TreeNode"/> cannot have parent of a given type.
    /// Types are connected by OR operator.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class CannotHaveParentAttribute : Attribute
    {
        public Type[] ParentType { get; }

        public CannotHaveParentAttribute(params Type[] parent)
        {
            ParentType = parent;
        }
    }
}
