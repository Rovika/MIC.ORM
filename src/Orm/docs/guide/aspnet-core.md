# Setup in ASP.NET Core

The `EntityApp` is created, initialized and connected once, at startup, before the host is built: see
[Application startup](startup.md). It lives for the life of the process, so register it as a singleton.

A web application then needs one `OperationContext` per request, created at the start of the pipeline and passed
to all the code that serves the request, as its main container of context and state. Register `OperationContext`
as a scoped service. The container then creates one context per request, gives it to every handler and service
that asks for it, and disposes it when the request ends.

```csharp
var builder = WebApplication.CreateBuilder(args);

var shopApp = new ShopApp();
shopApp.Init();
await shopApp.ConnectToAsync(new DbSettings(new PostgresProvider(), connectionString));

builder.Services.AddSingleton(shopApp);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(services => {
  var httpContext = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
  return new OperationContext(shopApp, GetUserInfo(httpContext?.User)) {
    Services = services,
    HttpContext = httpContext,
    WebRequestId = httpContext?.TraceIdentifier,
    ConnectionReuseMode = DbConnectionReuseMode.KeepOpen,
  };
});

var webApp = builder.Build();
shopApp.HostServices = webApp.Services;
```

* `GetUserInfo` is the application's own function. It builds a `UserInfo` from the claims of the authenticated
  user and returns `UserInfo.Anonymous` when there is none. The container creates the context on its first use
  in the request; a handler runs after the authentication middleware, so the user is known by then.
* `Services = services` gives the context the service provider of the request. `context.GetService<T>()` looks
  among the application's own services first, then in the request's provider, then in the root provider set on
  `EntityApp.HostServices`. Code that holds the context reaches every service through it, the ORM's and the
  host's, the request-scoped ones included.
* `ConnectionReuseMode.KeepOpen` keeps a session's connection open until the context is disposed, at the end of
  the request. With the default, `NoReuse`, a session closes its connection after each operation.
* A multi-tenant application sets the tenant in the same place, from a claim of the token:
  `SetValue("TenantId", …)`. See [Multi-tenancy extension](multi-tenancy.md).

A handler takes the context as a parameter:

```csharp
webApp.MapGet("/customers/{id}/orders", async (Guid id, OperationContext context) => {
  var session = context.OpenSession();
  return await session.EntitySet<IOrder>()
    .Where(o => o.Customer.Id == id)
    .Select(o => new { o.Id, o.PlacedOn, o.Total })
    .ToListAsync();
});
```

The context and the session are on [Core runtime objects](core-objects.md).
