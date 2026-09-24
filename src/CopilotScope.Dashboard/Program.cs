using CopilotScope.Dashboard;

// Everything the dashboard does is built in DashboardApp, so the native `copilotscope` host
// (ADR-004) can run the same application inside its own process.
var app = DashboardApp.Build(new WebApplicationOptions { Args = args });
app.Run();
