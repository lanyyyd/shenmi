package com.autobackup;

import com.mojang.brigadier.CommandDispatcher;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.commands.Commands;
import net.minecraft.network.chat.Component;

import java.nio.file.Path;
import java.util.List;

public final class BackupCommand {
    private BackupCommand() {
    }

    public static void register(CommandDispatcher<CommandSourceStack> dispatcher) {
        dispatcher.register(Commands.literal("autobackup")
                .requires(source -> source.hasPermission(2))
                .executes(context -> status(context.getSource()))
                .then(Commands.literal("now").executes(context -> now(context.getSource())))
                .then(Commands.literal("list").executes(context -> list(context.getSource())))
                .then(Commands.literal("reload").executes(context -> reload(context.getSource()))));
    }

    private static int status(CommandSourceStack source) {
        BackupManager manager = BackupManager.INSTANCE;
        source.sendSuccess(() -> Component.literal("===== Auto Backup ====="), false);
        source.sendSuccess(() -> Component.literal("定时备份：" + manager.scheduleText()), false);
        source.sendSuccess(() -> Component.literal("保留规则：" + manager.retentionText()), false);
        source.sendSuccess(() -> Component.literal("上次尝试："
                + BackupManager.formatTime(manager.lastAttemptMillis())), false);
        if (manager.lastSuccessMillis() > 0) {
            source.sendSuccess(() -> Component.literal("上次成功："
                    + BackupManager.formatTime(manager.lastSuccessMillis())), false);
        }
        source.sendSuccess(() -> Component.literal("上次结果：" + manager.lastResult()), false);
        source.sendSuccess(() -> Component.literal("备份目录：" + manager.backupDirectory()), false);
        return 1;
    }

    private static int now(CommandSourceStack source) {
        BackupManager manager = BackupManager.INSTANCE;
        if (manager.isBusy()) {
            source.sendFailure(Component.literal("已有备份任务正在执行，请稍后再试"));
            return 0;
        }
        source.sendSuccess(() -> Component.literal("开始备份存档..."), false);
        manager.requestBackup(source.getServer(), BackupManager.Trigger.MANUAL, result -> {
            if (result.success()) {
                source.sendSuccess(() -> Component.literal(result.message()), false);
            } else {
                source.sendFailure(Component.literal(result.message()));
            }
        });
        return 1;
    }

    private static int list(CommandSourceStack source) {
        List<Path> backups = BackupManager.INSTANCE.listBackups();
        if (backups.isEmpty()) {
            source.sendSuccess(() -> Component.literal("还没有任何备份"), false);
            return 1;
        }
        int limit = Math.min(10, backups.size());
        source.sendSuccess(() -> Component.literal("最近 " + limit + " 份备份："), false);
        for (int i = 0; i < limit; i++) {
            Path path = backups.get(i);
            long size;
            try {
                size = java.nio.file.Files.size(path);
            } catch (Exception e) {
                size = 0;
            }
            long finalSize = size;
            source.sendSuccess(() -> Component.literal(" - " + path.getFileName()
                    + "（" + BackupManager.formatBytes(finalSize) + "）"), false);
        }
        return 1;
    }

    private static int reload(CommandSourceStack source) {
        BackupManager.INSTANCE.reloadConfig();
        source.sendSuccess(() -> Component.literal("Auto Backup 配置已重新加载"), false);
        return 1;
    }
}
