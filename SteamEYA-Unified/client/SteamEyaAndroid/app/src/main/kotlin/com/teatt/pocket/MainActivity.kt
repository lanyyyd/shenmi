package com.teatt.pocket

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import com.teatt.pocket.core.Session
import com.teatt.pocket.feature.LoginScreen
import com.teatt.pocket.feature.RootScreen
import com.teatt.pocket.ui.PocketTheme

class MainActivity : ComponentActivity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        setContent {
            PocketTheme {
                val state by Session.state.collectAsState()

                Surface(
                    modifier = Modifier.fillMaxSize(),
                    color = MaterialTheme.colorScheme.background
                ) {
                    AnimatedContent(
                        targetState = state.loggedIn,
                        transitionSpec = {
                            fadeIn(tween(260)) togetherWith fadeOut(tween(180))
                        },
                        label = "authSwitch"
                    ) { loggedIn ->
                        if (loggedIn) {
                            RootScreen(onLoggedOut = { /* Session.clear() 已在页面内调用 */ })
                        } else {
                            LoginScreen(onLoggedIn = { /* Session.save 后 StateFlow 自动切换 */ })
                        }
                    }
                }
            }
        }
    }
}
