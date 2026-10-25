plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// 版本号由 CI 注入：-PappVersionName=1.0.13 -PappVersionCode=13
val appVersionName = (project.findProperty("appVersionName") as String?) ?: "1.0.0"
val appVersionCode = ((project.findProperty("appVersionCode") as String?) ?: "1").toInt()

// 固定签名：keystore 跟着仓库走，保证每次构建签名一致，新版本可以直接覆盖安装。
// CI 想换成自己的密钥时，配 ANDROID_KEYSTORE_PATH / ANDROID_KEYSTORE_PASSWORD 等环境变量即可覆盖。
val fixedKeystore = rootProject.layout.projectDirectory.file("keystore/teatt-release.jks").asFile

// 注意：没配置的 GitHub secret 在 runner 上会变成空字符串（不是 null），
// 直接写 `?: default` 兜不住，会把 alias/密码覆盖成 ""，打包阶段就会报 "keystore password was incorrect"。
fun signingEnv(key: String, fallback: String): String =
    System.getenv(key)?.takeIf { it.isNotBlank() } ?: fallback

android {
    namespace = "com.teatt.pocket"
    compileSdk = 37

    defaultConfig {
        applicationId = "com.teatt.pocket"
        minSdk = 26
        targetSdk = 37
        versionCode = appVersionCode
        versionName = appVersionName
    }

    signingConfigs {
        create("release") {
            val envPath = System.getenv("ANDROID_KEYSTORE_PATH")
            storeFile = if (!envPath.isNullOrBlank()) file(envPath) else fixedKeystore
            storePassword = signingEnv("ANDROID_KEYSTORE_PASSWORD", "shenmi2026")
            keyAlias = signingEnv("ANDROID_KEY_ALIAS", "teatt")
            keyPassword = signingEnv("ANDROID_KEY_PASSWORD", "shenmi2026")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            isShrinkResources = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            // release 一律用固定签名，不再回退 debug（debug 签名每次换机器都会变，装不上覆盖包）
            signingConfig = signingConfigs.getByName("release")
        }
        debug {
            applicationIdSuffix = ".debug"
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
        // WireGuard 的 tunnel 库用到了 java.time / java.util.stream，
        // 本项目 minSdk 26 —— 这两类 API 在 Android 8.0 已原生提供，无需 core library desugaring，
        // 因此不引入 desugar_jdk_libs（避免与 AGP 9.x 的版本兼容问题）。
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    packaging {
        resources.excludes += setOf("/META-INF/{AL2.0,LGPL2.1}")
    }
}

android {
    sourceSets["main"].java.srcDirs("src/main/kotlin")
}

dependencies {
    implementation(libs.core.ktx)
    implementation(libs.activity.compose)
    implementation(libs.lifecycle.runtime.compose)
    implementation(libs.lifecycle.viewmodel.compose)
    implementation(libs.datastore.preferences)
    implementation(libs.webkit)

    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.graphics)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons)
    debugImplementation(libs.compose.ui.tooling)

    implementation(libs.okhttp)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.coil.compose)
    implementation(libs.coil.network.okhttp)

    // 液态玻璃：底部功能栏的背景折射与高光
    implementation(libs.backdrop)
    implementation(libs.kyant.shapes)

    // 「小工具」里的 WireGuard VPN：官方用户态隧道库（GoBackend），不需要 root / NDK
    implementation(libs.wireguard.tunnel)
}
