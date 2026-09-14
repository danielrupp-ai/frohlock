using FrohLock.Core;
using FrohLock.Core.Config;
using FrohLock.Core.Crypto;
using FrohLock.Core.Logging;
using FrohLock.Service;
using FrohLock.Service.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

AppPaths.EnsureDataDir();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = AppPaths.ServiceName);

// Kern-Dienste als Singletons.
builder.Services.AddSingleton(sp => new AuditLog(AppPaths.AuditLogPath));
builder.Services.AddSingleton(sp => new RsaSignatureVerifier(EmbeddedKeys.SigningPublicKeyPem));
builder.Services.AddSingleton(sp => RuntimeState.Load());

builder.Services.AddSingleton(sp => new FrohLock.Core.Time.TrustedTimeProvider(
    AppPaths.TrustedTimeStatePath,
    () => sp.GetRequiredService<SignedConfigStore>().Load()?.ServerBaseUrl));

builder.Services.AddSingleton(sp => new SignedConfigStore(
    AppPaths.ConfigEnvelopePath, sp.GetRequiredService<RsaSignatureVerifier>()));

builder.Services.AddSingleton(sp => new EnforcementController(
    sp.GetRequiredService<FrohLock.Core.Time.TrustedTimeProvider>(),
    sp.GetRequiredService<AuditLog>(),
    sp.GetRequiredService<RuntimeState>()));

// IPC-Server (Agent <-> Dienst). Agent-Lebenszeichen an Controller melden.
builder.Services.AddSingleton<IHostedService>(sp => new IpcServer(
    sp.GetRequiredService<EnforcementController>(),
    sp.GetRequiredService<ILogger<IpcServer>>(),
    () => sp.GetRequiredService<EnforcementController>().MarkAgentAlive()));

// Haupt-Worker.
builder.Services.AddSingleton<IHostedService>(sp => new EnforcementWorker(
    sp.GetRequiredService<EnforcementController>(),
    sp.GetRequiredService<SignedConfigStore>(),
    sp.GetRequiredService<FrohLock.Core.Time.TrustedTimeProvider>(),
    sp.GetRequiredService<RsaSignatureVerifier>(),
    sp.GetRequiredService<AuditLog>(),
    sp.GetRequiredService<RuntimeState>(),
    sp.GetRequiredService<ILogger<EnforcementWorker>>()));

var host = builder.Build();
host.Run();
