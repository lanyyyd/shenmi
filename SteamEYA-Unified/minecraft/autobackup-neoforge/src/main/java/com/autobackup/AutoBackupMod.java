package com.autobackup;

import net.neoforged.fml.common.Mod;
import net.neoforged.neoforge.common.NeoForge;
import net.neoforged.neoforge.event.RegisterCommandsEvent;
import net.neoforged.neoforge.event.server.ServerStartedEvent;
import net.neoforged.neoforge.event.server.ServerStoppingEvent;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

@Mod(AutoBackupMod.MOD_ID)
public final class AutoBackupMod {
    public static final String MOD_ID = "autobackup";
    public static final Logger LOGGER = LoggerFactory.getLogger(MOD_ID);

    public AutoBackupMod() {
        NeoForge.EVENT_BUS.addListener(this::onServerStarted);
        NeoForge.EVENT_BUS.addListener(this::onServerStopping);
        NeoForge.EVENT_BUS.addListener(this::onRegisterCommands);
    }

    private void onServerStarted(ServerStartedEvent event) {
        BackupManager.INSTANCE.onServerStarted(event.getServer());
    }

    private void onServerStopping(ServerStoppingEvent event) {
        BackupManager.INSTANCE.onServerStopping(event.getServer());
    }

    private void onRegisterCommands(RegisterCommandsEvent event) {
        BackupCommand.register(event.getDispatcher());
    }
}
