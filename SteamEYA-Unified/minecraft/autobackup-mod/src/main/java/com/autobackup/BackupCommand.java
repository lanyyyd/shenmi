package com.autobackup;

import com.mojang.brigadier.CommandDispatcher;
import com.mojang.brigadier.context.CommandContext;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.command.CommandManager;
import net.minecraft.server.command.ServerCommandSource;
import net.minecraft.text.Text;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;

public final class BackupCommand {
    private BackupCommand() {
    }

    public static void register(CommandDispatcher<ServerCommandSource> dispatcher) {
        dispatcher.register(CommandManager.literal("autobackup")
                .requires(source -> source.hasPermissionLevel(2))
                .executes(context -> status(context.getSource()))
                .then(CommandManager.literal("now")
                        .executes(BackupCommand::now))
                .then(CommandManager.literal("list")
                        .executes(BackupCommand::list))
                .then(CommandManager.literal("status")
                        .executes(context -> status(context.getSource())))
                .then(CommandManager.literal("reload")
                        .executes(BackupCommand::reload)));
    }

    private static int now(CommandContext<ServerCommandSource> context) {
        ServerCommandSource source = context.getSource();
        MinecraftServer server = source.getServer();
        if (BackupManager.INSTANCE.isBusy()) {
            source.sendError(Text.literal("已有备份任务正在执行，请稍后再试"));
            return 0;
        }

        source.sendFeedback(() -> Text.literal("开始备份存档..."), false);
        BackupManager.INSTANCE.requestBackup(server, BackupManager.Trigger.MANUAL, result -> {
            if (result.success()) {
                source.sendFeedback(() -> Text.literal(result.message()), false);
            } else {
                source.sendError(Text.literal(result.message()));
            }
        });
        return 1;
    }

    private static int list(CommandContext<ServerCommandSource> context) {
        ServerCommandSource source = context.getSource();
        List<Path> backups = BackupManager.INSTANCE.listBackups();
        if (backups.isEmpty()) {
            source.sendFeedback(() -> Text.literal("暂无本地备份"), false);
            return 0;
        }

        source.sendFeedback(() -> Text.literal("共 " + backups.size() + " 份本地备份（最多显示 15 条）："), false);
        int shown = Math.min(backups.size(), 15);
        for (int i = 0; i < shown; i++) {
            Path path = backups.get(i);
            String line = (i + 1) + ". " + path.getFileName()
                    + "  " + BackupManager.formatBytes(size(path))
                    + "  " + BackupManager.formatTime(lastModified(path));
            source.sendFeedback(() -> Text.literal(line), false);
        }
        if (backups.size() > shown) {
            source.sendFeedback(() -> Text.literal("其余 " + (backups.size() - shown) + " 份未显示"), false);
        }
        return 1;
    }

    private static int status(ServerCommandSource source) {
        BackupManager manager = BackupManager.INSTANCE;
        BackupConfig config = manager.config();

        source.sendFeedback(() -> Text.literal("=== Auto Backup 状态 ==="), false);

        String autoLine;
        if (!config.enabled) {
            autoLine = "自动备份：关闭";
        } else if (config.schedule.enabled && !config.schedule.slots.isEmpty()) {
            autoLine = "自动备份：按本地时间 " + manager.scheduleText();
        } else if (config.intervalMinutes <= 0) {
            autoLine = "自动备份：开启，未启用定时备份";
        } else {
            long elapsed = System.currentTimeMillis() - manager.lastAttemptMillis();
            long remainMillis = Math.max(0L, config.intervalMinutes * 60_000L - elapsed);
            long remainMinutes = Math.max(1L, (remainMillis + 59_999L) / 60_000L);
            autoLine = "自动备份：开启，每 " + config.intervalMinutes
                    + " 分钟一次（约 " + remainMinutes + " 分钟后）";
        }
        final String auto = autoLine;
        source.sendFeedback(() -> Text.literal(auto), false);

        source.sendFeedback(() -> Text.literal("保留规则：" + manager.retentionText()
                + "，压缩级别：" + config.compressLevel
                + "，启动备份：" + (config.backupOnStart ? "开" : "关")
                + "，关服备份：" + (config.backupOnStop ? "开" : "关")), false);

        String uploadLine;
        if (!config.upload.enabled) {
            uploadLine = "上传：关闭";
        } else if (config.upload.url.isBlank()) {
            uploadLine = "上传：已开启，但未配置 url";
        } else {
            uploadLine = "上传：开启 -> " + config.upload.url;
        }
        final String upload = uploadLine;
        source.sendFeedback(() -> Text.literal(upload), false);

        String last = manager.lastResultMillis() == 0
                ? "最近结果：无"
                : "最近结果：" + BackupManager.formatTime(manager.lastResultMillis())
                + " " + manager.lastResult();
        source.sendFeedback(() -> Text.literal(last), false);

        source.sendFeedback(() -> Text.literal("备份目录："
                + manager.backupDirectory().toAbsolutePath()), false);
        return 1;
    }

    private static int reload(CommandContext<ServerCommandSource> context) {
        BackupManager.INSTANCE.reloadConfig();
        context.getSource().sendFeedback(() -> Text.literal("配置已重新加载："
                + BackupConfig.path().toAbsolutePath()), false);
        return 1;
    }

    private static long size(Path path) {
        try {
            return Files.size(path);
        } catch (IOException e) {
            return 0L;
        }
    }

    private static long lastModified(Path path) {
        try {
            return Files.getLastModifiedTime(path).toMillis();
        } catch (IOException e) {
            return 0L;
        }
    }
}
