plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val testBuild: Boolean = (findProperty("frohlockTestBuild") as String?) == "true"
if (testBuild && !System.getenv("FROHLOCK_KEYSTORE").isNullOrBlank())
    throw GradleException("Test-Build (USB-Debugging erlaubt) darf NIE mit dem Release-Schlüssel signiert werden")

// Version kommt aus dem Git-Tag (CI: -PfrohlockVersion=1.2.3), lokal Standard.
val appVersion: String = (findProperty("frohlockVersion") as String?)?.removePrefix("android-v") ?: "1.0.0"
val appVersionCode: Int = appVersion.split(".").map { it.toIntOrNull() ?: 0 }
    .let { (it + listOf(0, 0, 0)).take(3) }
    .let { (a, b, c) -> a * 10000 + b * 100 + c }

android {
    namespace = "de.froehlichdienste.frohlock"
    compileSdk = 35

    defaultConfig {
        applicationId = "de.froehlichdienste.frohlock"
        minSdk = 26
        targetSdk = 35
        versionCode = appVersionCode
        versionName = appVersion
        // Nur für Emulator-Tests: USB-Debugging bleibt erlaubt (-PfrohlockTestBuild=true).
        buildConfigField("boolean", "ALLOW_DEBUGGING", if (testBuild) "true" else "false")
    }

    // Release-Schlüssel NUR aus Umgebungsvariablen (CI-Secret / lokal), nie im Repo.
    // Achtung: Jedes Update MUSS mit demselben Schlüssel signiert sein, sonst lehnt Android es ab
    // und der QR-Code (Signatur-Prüfsumme) passt nicht mehr.
    val ksPath = System.getenv("FROHLOCK_KEYSTORE")
    signingConfigs {
        if (!ksPath.isNullOrBlank()) {
            create("release") {
                storeFile = file(ksPath)
                storePassword = System.getenv("FROHLOCK_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("FROHLOCK_KEY_ALIAS") ?: "frohlock"
                keyPassword = System.getenv("FROHLOCK_KEY_PASSWORD") ?: System.getenv("FROHLOCK_KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = if (!ksPath.isNullOrBlank()) signingConfigs.getByName("release")
                            else signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { buildConfig = true }

    testOptions { unitTests.isReturnDefaultValues = true }

    lint {
        abortOnError = true
        // Texte sind bewusst deutsch im Code (eine Sprache, kindgerecht formuliert).
        disable += setOf("SetTextI18n", "StaticFieldLeak", "MonochromeLauncherIcon", "ObsoleteSdkInt")
    }
}

dependencies {
    // Bewusst ohne AndroidX/Fremdbibliotheken: weniger Angriffsfläche, kleines APK.
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
}
