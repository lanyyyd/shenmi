package com.autobackup;

import net.fabricmc.api.ModInitializer;
import net.fabricmc.fabric.api.command.v2.CommandRegistrationCallback;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

public class AutoBackupMod implements ModInitializer {
    public static final String MOD_ID = "autobackup";
    public static final Logger LOGGER = LoggerFactory.getLogger(MOD_ID);

    @Override
    public void onInitialize() {
        ServerLifecycleEvents.SERVER_STARTED.register(BackupManager.INSTANCE::onServerStarted);
        ServerLifecycleEvents.SERVER_STOPPING.register(BackupManager.INSTANCE::onServerStopping);
        ServerTickEvents.END_SERVER_TICK.register(BackupManager.INSTANCE::onServerTick);
        CommandRegistrationCallback.EVENT.register((dispatcher, registryAccess, environment) ->
                BackupCommand.register(dispatcher));

        LOGGER.info("Auto Backup 初始化完成");
    }
}
