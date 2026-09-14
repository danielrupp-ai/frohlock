using System.Windows;
using FrohLock.Core;
using FrohLock.Core.Config;
using FrohLock.Core.Crypto;

namespace FrohLock.Setup;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Deinstallations-Schutz: der Inno-Uninstaller ruft "FrohLockSetup.exe --verify-pin".
        // Exitcode 0 = PIN korrekt (Deinstallation erlaubt), sonst != 0.
        if (e.Args.Any(a => string.Equals(a, "--verify-pin", StringComparison.OrdinalIgnoreCase)))
        {
            int code = RunVerifyPin();
            Shutdown(code);
            return;
        }

        new MainWindow().Show();
    }

    private int RunVerifyPin()
    {
        try
        {
            var verifier = new RsaSignatureVerifier(EmbeddedKeys.SigningPublicKeyPem);
            var store = new SignedConfigStore(AppPaths.ConfigEnvelopePath, verifier);
            var cfg = store.Load();
            if (cfg is null || string.IsNullOrEmpty(cfg.PinHash))
            {
                // Kein gültiger PIN hinterlegt -> Deinstallation zulassen (nicht aussperren).
                return 0;
            }

            var dlg = new PinPromptWindow();
            bool? ok = dlg.ShowDialog();
            if (ok != true) return 2; // abgebrochen

            return PinHasher.Verify(dlg.Pin, cfg.PinHash, cfg.PinSalt, cfg.PinIterations) ? 0 : 1;
        }
        catch
        {
            return 3;
        }
    }
}
