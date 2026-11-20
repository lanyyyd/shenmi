package com.autobackup;

import net.minecraft.network.chat.Component;
import net.minecraft.server.MinecraftServer;
import net.minecraft.world.level.storage.LevelResource;
import org.slf4j.Logger;

import java.io.BufferedOutputStream;
import java.io.File;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.time.Instant;
import java.time.LocalDate;
import java.time.LocalDateTime;
import java.time.LocalTime;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.function.Consumer;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Stream;
import java.util.zip.ZipEntry;
import java.util.zip.ZipOutputStream;

/**
 * Forge / NeoForge 共用实现：使用 Mojang 官方映射，定时检查用 JDK 调度器，
 * 不依赖各加载器的 tick 事件。
 */
public final class BackupManager {
    public static final BackupManager INSTANCE = new BackupManager();

    public enum Trigger {
        MANUAL, SCHEDULED, STARTUP, SHUTDOWN
    }

    public record BackupResult(boolean success, String message, Path file, long bytes) {
    }

    private static final Logger LOGGER = AutoBackupMod.LOGGER;
    private static final DateTimeFormatter FILE_TIME = DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss");
    private static final DateTimeFormatter DISPLAY_TIME = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss");
    private static final Pattern BACKUP_NAME =
            Pattern.compile("^(.*)-(\\d{8}-\\d{6})(?:-(\\d+))?\\.zip$");
    private static final Pattern VERSION_DIR =
            Pattern.compile("[\\\\/]versions[\\\\/]([^\\\\/]+)[\\\\/]", Pattern.CASE_INSENSITIVE);

    private final AtomicBoolean busy = new AtomicBoolean(false);
    private final ExecutorService worker = Executors.newSingleThreadExecutor(runnable -> {
        Thread thread = new Thread(runnable, "AutoBackup-Worker");
        thread.setDaemon(true);
        return thread;
    });

    private volatile BackupConfig config = BackupConfig.load();
    private volatile MinecraftServer server;
    private volatile ScheduledExecutorService scheduler;

    private volatile long lastAttemptMillis;
    private volatile long lastSuccessMillis;
    private volatile long lastResultMillis;
    private volatile String lastResult = "尚未备份";
    /** 上传进度：起始时间、上次聊天播报时间、当前存档标签。 */
    private volatile long uploadStartMillis;
    private volatile long uploadLastReportMillis;
    private volatile String uploadLabel = "";
    /** 已执行过的定时槽位，key = 日期#槽位下标。 */
    private final Set<String> ranSlots = new HashSet<>();

    private BackupManager() {
    }

    public BackupConfig config() {
        return config;
    }

    public Path backupDirectory() {
        return BackupConfig.gameDir().resolve("autobackups").toAbsolutePath().normalize();
    }

    public boolean isBusy() {
        return busy.get();
    }

    public long lastAttemptMillis() {
        return lastAttemptMillis;
    }

    public long lastSuccessMillis() {
        return lastSuccessMillis;
    }

    public long lastResultMillis() {
        return lastResultMillis;
    }

    public String lastResult() {
        return lastResult;
    }

    public void onServerStarted(MinecraftServer server) {
        this.server = server;
        this.config = BackupConfig.load();
        this.ranSlots.clear();
        this.lastAttemptMillis = System.currentTimeMillis();

        LOGGER.info("Auto Backup 已加载，备份目录: {}", backupDirectory());
        LOGGER.info("定时备份: {}，压缩级别: {}，上传: {}",
                config.enabled && config.schedule.enabled ? describeSchedule() : "关闭",
                config.compressLevel,
                config.upload.enabled ? config.upload.url : "关闭");

        startScheduler();
    }

    private void startScheduler() {
        stopScheduler();
        ScheduledExecutorService service = Executors.newSingleThreadScheduledExecutor(runnable -> {
            Thread thread = new Thread(runnable, "AutoBackup-Scheduler");
            thread.setDaemon(true);
            return thread;
        });
        scheduler = service;
        service.scheduleWithFixedDelay(this::checkSchedule, 5, 15, TimeUnit.SECONDS);
        if (config.enabled && config.backupOnStart) {
            service.schedule(() -> {
                MinecraftServer current = server;
                if (current != null) {
                    LOGGER.info("执行启动备份");
                    requestBackup(current, Trigger.STARTUP, null);
                }
            }, 10, TimeUnit.SECONDS);
        }
    }

    private void stopScheduler() {
        ScheduledExecutorService service = scheduler;
        scheduler = null;
        if (service != null) {
            service.shutdownNow();
        }
    }

    private void checkSchedule() {
        MinecraftServer current = server;
        if (current == null || !config.enabled || busy.get()) {
            return;
        }
        try {
            if (!config.schedule.enabled) {
                checkInterval(current);
                return;
            }
            if (config.schedule.slots.isEmpty()) {
                return;
            }
            LocalDateTime now = LocalDateTime.now();
            LocalDate today = now.toLocalDate();
            LocalTime time = now.toLocalTime();
            for (int i = 0; i < config.schedule.slots.size(); i++) {
                BackupConfig.Slot slot = config.schedule.slots.get(i);
                if (time.isBefore(slot.startTime()) || !time.isBefore(slot.endTime())) {
                    continue;
                }
                if (time.isBefore(slot.atTime())) {
                    continue;
                }
                String key = today + "#" + i;
                if (ranSlots.contains(key)) {
                    continue;
                }
                ranSlots.add(key);
                ranSlots.removeIf(entry -> !entry.startsWith(today.toString()));
                LOGGER.info("到达定时备份时间 {}（窗口 {}-{}）", slot.at, slot.start, slot.end);
                requestBackup(current, Trigger.SCHEDULED, null);
                return;
            }
            ranSlots.removeIf(key -> !key.startsWith(today.toString()));
        } catch (Throwable t) {
            LOGGER.warn("定时备份检查出错: {}", t.getMessage());
        }
    }

    private void checkInterval(MinecraftServer current) {
        if (config.intervalMinutes <= 0) {
            return;
        }
        long interval = config.intervalMinutes * 60_000L;
        if (System.currentTimeMillis() - lastAttemptMillis >= interval) {
            LOGGER.info("触发定时备份（固定间隔）");
            requestBackup(current, Trigger.SCHEDULED, null);
        }
    }

    public void onServerStopping(MinecraftServer server) {
        stopScheduler();
        this.server = null;
        if (config.enabled && config.backupOnStop) {
            LOGGER.info("服务器关闭，执行关服备份");
            requestBackup(server, Trigger.SHUTDOWN, null, true);
        }
    }

    public void reloadConfig() {
        this.config = BackupConfig.load();
        LOGGER.info("配置已重新加载: {}", BackupConfig.path());
    }

    private String describeSchedule() {
        StringBuilder out = new StringBuilder();
        for (BackupConfig.Slot slot : config.schedule.slots) {
            if (out.length() > 0) {
                out.append(" / ");
            }
            out.append(slot.at).append("（").append(slot.start).append("-").append(slot.end).append("）");
        }
        return out.length() == 0 ? "未配置时间点" : out.toString();
    }

    public String scheduleText() {
        return describeSchedule();
    }

    public String retentionText() {
        StringBuilder out = new StringBuilder();
        out.append("最新 ").append(config.retention.latest).append(" 份");
        for (int days : config.retention.days) {
            out.append(" + ").append(days).append(" 天前最新 1 份");
        }
        return out.toString();
    }

    public void requestBackup(MinecraftServer server, Trigger trigger, Consumer<BackupResult> callback) {
        requestBackup(server, trigger, callback, false);
    }

    private void requestBackup(MinecraftServer server, Trigger trigger,
                               Consumer<BackupResult> callback, boolean synchronous) {
        if (!busy.compareAndSet(false, true)) {
            LOGGER.warn("忽略备份请求（{}）：已有备份任务正在执行", trigger);
            notify(server, callback, new BackupResult(false, "已有备份任务正在执行，请稍后再试", null, 0));
            return;
        }

        Runnable task = () -> startBackup(server, trigger, callback);
        if (synchronous) {
            task.run();
            return;
        }
        try {
            server.execute(task);
        } catch (Throwable t) {
            busy.set(false);
            notify(server, callback, new BackupResult(false, "无法调度备份任务：" + t.getMessage(), null, 0));
        }
    }

    private void startBackup(MinecraftServer server, Trigger trigger, Consumer<BackupResult> callback) {
        Path worldDir;
        try {
            worldDir = server.getWorldPath(LevelResource.ROOT).toAbsolutePath().normalize();
        } catch (Throwable t) {
            fail(server, callback, "无法定位存档目录：" + t.getMessage());
            return;
        }
        if (worldDir == null || !Files.isDirectory(worldDir)) {
            fail(server, callback, "存档目录不存在：" + worldDir);
            return;
        }

        try {
            server.getPlayerList().saveAll();
            if (!server.saveEverything(true, true, true)) {
                fail(server, callback, "保存世界失败，备份已取消");
                return;
            }
        } catch (Throwable t) {
            fail(server, callback, "保存世界时出错：" + t.getMessage());
            return;
        }

        String worldName = worldDir.getFileName() == null ? "world" : worldDir.getFileName().toString();
        String versionName = resolveVersionName(worldDir);
        String gameVersion = server.getServerVersion();
        String saveName = worldName;
        try {
            String levelName = server.getWorldData().getLevelName();
            if (levelName != null && !levelName.isBlank()) {
                saveName = levelName;
            }
        } catch (Throwable t) {
            LOGGER.debug("读取世界显示名失败，使用文件夹名: {}", t.getMessage());
        }
        final String finalSaveName = saveName;
        final String finalSavePath = worldDir.toString();
        Runnable job = () -> {
            BackupResult result;
            try {
                result = createZip(worldDir, worldName);
            } catch (Throwable t) {
                LOGGER.error("创建备份文件失败", t);
                result = new BackupResult(false, "备份失败：" + t.getMessage(), null, 0);
            }

            long now = System.currentTimeMillis();
            lastAttemptMillis = now;
            lastResultMillis = now;

            if (result.success()) {
                lastSuccessMillis = now;
                applyRetention();
                if (config.upload.enabled) {
                    LOGGER.info("开始上传备份 {} -> {}", result.file().getFileName(), config.upload.url);
                    uploadLabel = finalSaveName + (versionName.isBlank() ? "" : "（" + versionName + "）");
                    uploadStartMillis = System.currentTimeMillis();
                    uploadLastReportMillis = uploadStartMillis;
                    broadcastAll(server, "开始上传存档 " + uploadLabel + "（" + formatBytes(result.bytes()) + "）");
                    BackupUploader.Metadata meta = new BackupUploader.Metadata(
                            versionName, gameVersion, finalSaveName, worldName, finalSavePath, now);
                    BackupUploader.Result upload = BackupUploader.upload(config.upload, result.file(), meta,
                            (sent, total) -> reportUploadProgress(server, sent, total));
                    if (upload.success()) {
                        LOGGER.info("备份上传成功: {}", upload.message());
                        broadcastAll(server, "存档 " + uploadLabel + " 上传成功");
                        result = new BackupResult(true,
                                result.message() + "，上传成功", result.file(), result.bytes());
                    } else {
                        LOGGER.warn("备份上传失败: {}", upload.message());
                        broadcastAll(server, "存档 " + uploadLabel + " 上传失败：" + upload.message());
                        result = new BackupResult(true,
                                result.message() + "，上传失败（" + upload.message() + "）",
                                result.file(), result.bytes());
                    }
                }
                LOGGER.info("备份完成: {} ({})", result.file(), formatBytes(result.bytes()));
            } else {
                LOGGER.warn("备份失败: {}", result.message());
            }

            lastResult = result.message();
            busy.set(false);
            notify(server, callback, result);
        };

        if (trigger == Trigger.SHUTDOWN) {
            job.run();
        } else {
            worker.submit(job);
        }
    }

    private BackupResult createZip(Path worldDir, String worldName) throws IOException {
        Path directory = backupDirectory();
        Files.createDirectories(directory);

        String baseName = worldName + "-" + LocalDateTime.now().format(FILE_TIME);
        Path target = directory.resolve(baseName + ".zip");
        int suffix = 1;
        while (Files.exists(target)) {
            target = directory.resolve(baseName + "-" + (suffix++) + ".zip");
        }
        Path temp = directory.resolve(target.getFileName() + ".tmp");

        long bytes;
        try {
            try (OutputStream fileOut = Files.newOutputStream(temp);
                 ZipOutputStream zip = new ZipOutputStream(new BufferedOutputStream(fileOut, 64 * 1024))) {
                zip.setLevel(config.compressLevel);
                try (Stream<Path> stream = Files.walk(worldDir)) {
                    Iterator<Path> iterator = stream.iterator();
                    while (iterator.hasNext()) {
                        Path path = iterator.next();
                        if (path.equals(worldDir)) {
                            continue;
                        }
                        String relative = worldDir.relativize(path).toString()
                                .replace(File.separatorChar, '/');
                        if (relative.equals("session.lock")) {
                            continue;
                        }
                        if (Files.isDirectory(path)) {
                            zip.putNextEntry(new ZipEntry(worldName + "/" + relative + "/"));
                            zip.closeEntry();
                        } else {
                            ZipEntry entry = new ZipEntry(worldName + "/" + relative);
                            entry.setTime(Files.getLastModifiedTime(path).toMillis());
                            zip.putNextEntry(entry);
                            Files.copy(path, zip);
                            zip.closeEntry();
                        }
                    }
                }
            }
            Files.move(temp, target, StandardCopyOption.REPLACE_EXISTING);
            bytes = Files.size(target);
        } catch (IOException | RuntimeException e) {
            try {
                Files.deleteIfExists(temp);
            } catch (IOException ignored) {
            }
            throw e;
        }

        return new BackupResult(true,
                "备份完成：" + target.getFileName() + "（" + formatBytes(bytes) + "）", target, bytes);
    }

    /** 同一个存档保留：最新 N 份 + 昨天最新 1 份 + 三天前最新 1 份（按配置的天数）。 */
    private void applyRetention() {
        List<Path> backups = listBackups();
        Map<String, List<Path>> groups = new LinkedHashMap<>();
        for (Path path : backups) {
            Matcher matcher = BACKUP_NAME.matcher(path.getFileName().toString());
            if (!matcher.matches()) {
                continue;
            }
            groups.computeIfAbsent(matcher.group(1), key -> new ArrayList<>()).add(path);
        }

        BackupConfig.Retention retention = config.retention;
        int latest = Math.max(1, retention.latest);
        LocalDate today = LocalDate.now();
        Set<Path> keep = new HashSet<>();

        for (List<Path> files : groups.values()) {
            List<Path> sorted = new ArrayList<>(files);
            sorted.sort(Comparator.comparingLong(BackupManager::lastModified).reversed());
            keep.addAll(sorted.subList(0, Math.min(latest, sorted.size())));

            for (int days : retention.days) {
                LocalDate target = today.minusDays(Math.max(0, days));
                for (Path path : sorted) {
                    if (keep.contains(path)) {
                        continue;
                    }
                    LocalDate day = Instant.ofEpochMilli(lastModified(path))
                            .atZone(ZoneId.systemDefault()).toLocalDate();
                    if (day.equals(target)) {
                        keep.add(path);
                        break;
                    }
                }
            }
        }

        for (Path path : backups) {
            if (keep.contains(path)) {
                continue;
            }
            try {
                Files.deleteIfExists(path);
                LOGGER.info("已按保留规则删除旧备份 {}", path.getFileName());
            } catch (IOException e) {
                LOGGER.warn("删除旧备份失败 {}: {}", path.getFileName(), e.getMessage());
            }
        }
    }

    private static String resolveVersionName(Path worldDir) {
        Matcher fromWorld = VERSION_DIR.matcher(worldDir.toAbsolutePath().toString());
        if (fromWorld.find()) {
            return fromWorld.group(1);
        }
        Matcher fromGame = VERSION_DIR.matcher(BackupConfig.gameDir().toString());
        if (fromGame.find()) {
            return fromGame.group(1);
        }
        return "默认版本";
    }

    public List<Path> listBackups() {
        Path directory = backupDirectory();
        if (!Files.isDirectory(directory)) {
            return List.of();
        }
        try (Stream<Path> stream = Files.list(directory)) {
            return stream
                    .filter(Files::isRegularFile)
                    .filter(path -> path.getFileName().toString().endsWith(".zip"))
                    .sorted(Comparator.comparingLong(BackupManager::lastModified).reversed())
                    .toList();
        } catch (IOException e) {
            LOGGER.warn("读取备份列表失败: {}", e.getMessage());
            return List.of();
        }
    }

    private static long lastModified(Path path) {
        try {
            return Files.getLastModifiedTime(path).toMillis();
        } catch (IOException e) {
            return 0L;
        }
    }

    private void fail(MinecraftServer server, Consumer<BackupResult> callback, String message) {
        long now = System.currentTimeMillis();
        lastAttemptMillis = now;
        lastResultMillis = now;
        lastResult = message;
        busy.set(false);
        LOGGER.warn(message);
        notify(server, callback, new BackupResult(false, message, null, 0));
    }

    private void notify(MinecraftServer server, Consumer<BackupResult> callback, BackupResult result) {
        if (callback == null) {
            return;
        }
        try {
            server.execute(() -> callback.accept(result));
        } catch (Throwable t) {
            LOGGER.debug("无法发送备份结果: {}", t.getMessage());
        }
    }

    /** 每 3 秒往聊天框播报一次上传进度、速度与预计剩余时间。 */
    private void reportUploadProgress(MinecraftServer server, long sent, long total) {
        long now = System.currentTimeMillis();
        if (now - uploadLastReportMillis < 3000L) {
            return;
        }
        uploadLastReportMillis = now;

        long elapsed = Math.max(1L, now - uploadStartMillis);
        double speed = sent * 1000.0 / elapsed;
        double percent = total <= 0 ? 0 : Math.min(100.0, sent * 100.0 / total);
        long remaining = Math.max(0L, total - sent);
        long etaSeconds = speed < 1 ? 0 : (long) Math.ceil(remaining / speed);

        broadcastAll(server, String.format("上传中 %s：%.1f%% · %s/s · 剩余 %s",
                uploadLabel, percent, formatBytes((long) speed), formatEta(etaSeconds)));
    }

    private static String formatEta(long seconds) {
        if (seconds <= 0) {
            return "即将完成";
        }
        if (seconds < 60) {
            return seconds + " 秒";
        }
        return (seconds / 60) + " 分 " + (seconds % 60) + " 秒";
    }

    /** 广播到所有玩家聊天框；没有玩家在线时只写日志。 */
    private void broadcastAll(MinecraftServer server, String message) {
        if (server == null) {
            return;
        }
        try {
            server.execute(() -> {
                try {
                    server.getPlayerList()
                            .broadcastSystemMessage(Component.literal("[AutoBackup] " + message), false);
                } catch (Throwable t) {
                    LOGGER.debug("发送备份进度到聊天框失败: {}", t.getMessage());
                }
            });
        } catch (Throwable t) {
            LOGGER.debug("无法调度备份进度消息: {}", t.getMessage());
        }
    }

    public static String formatBytes(long bytes) {
        if (bytes < 1024) {
            return bytes + " B";
        }
        if (bytes < 1024 * 1024) {
            return String.format("%.1f KB", bytes / 1024.0);
        }
        return String.format("%.1f MB", bytes / (1024.0 * 1024.0));
    }

    public static String formatTime(long millis) {
        return DISPLAY_TIME.format(LocalDateTime.ofInstant(Instant.ofEpochMilli(millis), ZoneId.systemDefault()));
    }
}
