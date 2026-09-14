using System.Windows;
using FrohLock.Core;
using FrohLock.Core.Config;
using FrohLock.Core.Crypto;
using FrohLock.Core.Models;

namespace FrohLock.Setup;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // 1) Deinstallations-Schutz: der Uninstaller ruft "FrohLockSetup.exe --verify-pin".
        //    Exit 0 = erlaubt (Master- oder Eltern-PIN korrekt), sonst != 0.
        if (e.Args.Any(a => string.Equals(a, "--verify-pin", StringComparison.OrdinalIgnoreCase)))
        {
            int code = RunVerifyPin();
            Shutdown(code);
            return;
        }

        // 2) Bereits eingerichtet? -> Einrichtung nur nach PIN-Eingabe erneut zulassen
        //    (verhindert, dass jemand ohne PIN neu koppelt / Einstellungen ändert).
        var existing = LoadConfig();
        if (existing is not null && !string.IsNullOrEmpty(existing.PinHash))
        {
            var dlg = new PinPromptWindow("FrohLock ist bereits eingerichtet. Zum Ändern bitte den Eltern-PIN eingeben:");
            bool? ok = dlg.ShowDialog();
            if (ok == true && VerifyPinStatic(dlg.Pin, existing))
            {
                new MainWindow().Show();
            }
            else
            {
                MessageBox.Show(
                    "FrohLock ist bereits eingerichtet. Einstellungen und Deinstallation sind nur mit dem Eltern-PIN möglich.",
                    "FrohLock", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
            }
            return;
        }

        // 3) Frische Einrichtung.
        new MainWindow().Show();
    }

    private static LockConfig? LoadConfig()
    {
        try
        {
            var verifier = new RsaSignatureVerifier(EmbeddedKeys.SigningPublicKeyPem);
            return new SignedConfigStore(AppPaths.ConfigEnvelopePath, verifier).Load();
        }
        catch { return null; }
    }

    /// <summary>PIN ist gültig, wenn es die Master-PIN ist ODER dem konfigurierten Eltern-PIN entspricht.</summary>
    public static bool VerifyPinStatic(string pin, LockConfig? cfg)
    {
        if (PinHasher.Verify(pin, Branding.MasterUnlockHash, Branding.MasterUnlockSalt, Branding.MasterUnlockIterations))
            return true;
        if (cfg is not null && !string.IsNullOrEmpty(cfg.PinHash))
            return PinHasher.Verify(pin, cfg.PinHash, cfg.PinSalt, cfg.PinIterations);
        return false;
    }

    private int RunVerifyPin()
    {
        try
        {
            var cfg = LoadConfig();
            var dlg = new UninstallGateWindow(cfg);   // Popup: Standard „Nein", Entfernen nur mit PIN
            dlg.ShowDialog();
            return dlg.Allowed ? 0 : 1;               // 0 = erlaubt (PIN korrekt), sonst abbrechen
        }
        catch
        {
            return 3;
        }
    }
}
