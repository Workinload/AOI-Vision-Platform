using System.Runtime.CompilerServices;

// The fail-closed input-stability check needs a deterministic seam so a test can mutate
// an input at the exact moment a concurrent writer would (CompareWithHook). Production
// callers only use the public Compare API; nothing here widens the tool's behaviour.
[assembly: InternalsVisibleTo("AOI.Tests")]
