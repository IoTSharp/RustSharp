namespace RustSharp.Semantics;

public static partial class SafeCoreTypeAnalysis
{
    private sealed partial class Checker
    {
        private void ValidateStaticLifetimePosition(SafeCoreHirNode node)
        {
            // Named lifetime parameters remain outside the monomorphic profile.
            // Static lifetime contracts are represented for direct declarations;
            // nested explicit contracts must not silently lose their spelling.
            foreach (SafeCoreHirNode parent in _hir.Nodes)
            {
                Step(parent, 0);
                if (!parent.ChildIds.Contains(node.Id)) continue;
                if (parent.Kind is not (SafeCoreHirNodeKind.Function or SafeCoreHirNodeKind.Parameter or
                    SafeCoreHirNodeKind.Field or SafeCoreHirNodeKind.LetStatement or SafeCoreHirNodeKind.Const)) Unsupported(node);
                return;
            }
            Unsupported(node);
        }

        private void ValidateFieldLifetimes(SafeCoreHirNode node, int depth)
        {
            Step(node, depth);
            if (node.Kind == SafeCoreHirNodeKind.ReferenceType && node.Value is null)
                Fail(node, "RST2001", "A declared reference field requires an explicit lifetime; only static field lifetimes are supported in this profile.");
            foreach (SafeCoreHirNode child in Parts(node))
                if (IsType(child)) ValidateFieldLifetimes(child, depth + 1);
        }
    }
}
