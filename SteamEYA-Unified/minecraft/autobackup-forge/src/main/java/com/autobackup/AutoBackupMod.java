package com.autobackup;

import net.minecraftforge.common.MinecraftForge;
import net.minecraftforge.event.RegisterCommandsEvent;
import net.minecraftforge.event.server.ServerStartedEvent;
import net.minecraftforge.event.server.ServerStoppingEvent;
import net.minecraftforge.fml.common.Mod;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

@Mod(AutoBackupMod.MOD_ID)
public final class AutoBackupMod {
    public static final String MOD_ID = "autobackup";
    public static final Logger LOGGER = LoggerFactory.getLogger(MOD_ID);

    public AutoBackupMod() {
        MinecraftForge.EVENT_BUS.addListener(this::onServerStarted);
        MinecraftForge.EVENT_BUS.addListener(this::onServerStopping);
        MinecraftForge.EVENT_BUS.addListener(this::onRegisterCommands);
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
