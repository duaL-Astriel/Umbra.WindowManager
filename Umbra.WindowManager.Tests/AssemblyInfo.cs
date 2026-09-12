using Xunit;

// Una.Drawing's static QuerySelectorParser has a non-thread-safe cache, so tests interacting with
// widgets and stylesheets must run sequentially to avoid concurrent dictionary mutation errors.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
