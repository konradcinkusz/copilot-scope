using CopilotScope.Collector;

// Everything the collector does is built in CollectorApp, so the native `copilotscope` host
// (ADR-004) can run the same application inside its own process.
var app = await CollectorApp.BuildAsync(new WebApplicationOptions { Args = args });
await app.RunAsync();
