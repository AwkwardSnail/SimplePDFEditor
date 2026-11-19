global using Xunit;

// PDFium is not thread-safe, even across separate documents, so tests must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
