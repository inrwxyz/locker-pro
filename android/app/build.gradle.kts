plugins {
    alias(libs.plugins.android.application)
}

android {
    namespace = "com.inex.locker.companion"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.inex.locker.companion"
        minSdk = 24
        targetSdk = 35
        versionCode = 1
        versionName = "0.9.0-beta"
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_11
        targetCompatibility = JavaVersion.VERSION_11
    }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.appcompat)
    implementation(libs.material)
    implementation(libs.androidx.biometric)
    implementation("com.journeyapps:zxing-android-embedded:4.3.0")
}
