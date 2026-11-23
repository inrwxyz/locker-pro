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
        versionCode = 3
        versionName = "2.1.0"
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
    implementation("com.google.android.gms:play-services-code-scanner:16.1.0")
}
