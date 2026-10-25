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
            storePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD") ?: "shenmi2026"
            keyAlias = System.getenv("ANDROID_KEY_ALIAS") ?: "teatt"
            keyPassword = System.getenv("ANDROID_KEY_PASSWORD") ?: "shenmi2026"
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
}
