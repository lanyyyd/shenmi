package com.autobackup;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import net.fabricmc.loader.api.FabricLoader;

import java.io.IOException;
import java.io.Reader;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.LocalTime;
import java.util.ArrayList;
import java.util.List;

public class BackupConfig {
    private static final Gson GSON = new GsonBuilder().setPrettyPrinting().create();
    private static final Path CONFIG_PATH = FabricLoader.getInstance().getConfigDir().resolve("autobackup.json");

    /** 总开关：关闭后不再执行定时、启动与关服备份，手动备份仍可用。 */
    public boolean enabled = true;
    /** 旧版固定间隔（分钟）。schedule.enabled=true 时忽略。 */
    public int intervalMinutes = 0;
    /** 本地保留的备份份数（旧规则，schedule 模式下由 retention 接管）。 */
    public int keepBackups = 5;
    /** 服务器启动后是否自动备份一次。 */
    public boolean backupOnStart = false;
    /** 服务器关闭时是否同步备份一次。 */
    public boolean backupOnStop = false;
    /** zip 压缩级别 0-9。 */
    public int compressLevel = 6;

    public Upload upload = new Upload();
    public Schedule schedule = new Schedule();
    public Retention retention = new Retention();

    public static class Upload {
        public boolean enabled = false;
        /** 接收备份文件的 HTTP 地址。 */
        public String url = "";
        /** 可选的 Bearer Token，会放入 Authorization 请求头。 */
        public String token = "";
        /** 存档归属用户名（云存档按账号保存）。 */
        public String username = "";
        /** 固定为 minecraft，服务端只接受这个游戏。 */
        public String game = "minecraft";
        /** multipart/form-data 中文件字段名。 */
        public String fieldName = "file";
        /** 上传超时时间（秒）。 */
        public int timeoutSeconds = 600;
    }

    /** 按本地时间的三个备份时间窗。 */
    public static class Schedule {
        public boolean enabled = true;
        public List<Slot> slots = new ArrayList<>(List.of(
                new Slot("08:00", "10:00", "09:35"),
                new Slot("10:00", "12:00", "11:43"),
                new Slot("14:00", "17:00", "16:03")
        ));
    }

    public static class Slot {
        /** 时间窗开始，HH:mm。 */
        public String start = "08:00";
        /** 时间窗结束，HH:mm。 */
        public String end = "10:00";
        /** 真正执行备份的时刻，HH:mm。 */
        public String at = "09:35";

        public Slot() {
        }

        public Slot(String start, String end, String at) {
            this.start = start;
            this.end = end;
            this.at = at;
        }

        public LocalTime startTime() {
            return parse(start, LocalTime.MIN);
        }

        public LocalTime endTime() {
            return parse(end, LocalTime.MAX);
        }

        public LocalTime atTime() {
            return parse(at, LocalTime.NOON);
        }

        private static LocalTime parse(String value, LocalTime fallback) {
            try {
                return LocalTime.parse(value);
            } catch (Exception e) {
                return fallback;
            }
        }
    }

    /** 同一个存档保留：最新 N 份 + 指定天数前的最新 1 份。 */
    public static class Retention {
        public int latest = 3;
        public List<Integer> days = new ArrayList<>(List.of(1, 3));
    }

    public static BackupConfig load() {
        BackupConfig config = null;
        if (Files.isRegularFile(CONFIG_PATH)) {
            try (Reader reader = Files.newBufferedReader(CONFIG_PATH, StandardCharsets.UTF_8)) {
                config = GSON.fromJson(reader, BackupConfig.class);
            } catch (Exception e) {
                AutoBackupMod.LOGGER.warn("读取配置文件失败，将使用默认配置: {}", e.getMessage());
            }
        }
        if (config == null) {
            config = new BackupConfig();
        }
        config.validate();
        config.save();
        return config;
    }

    public void save() {
        try {
            Files.createDirectories(CONFIG_PATH.getParent());
            try (Writer writer = Files.newBufferedWriter(CONFIG_PATH, StandardCharsets.UTF_8)) {
                GSON.toJson(this, writer);
            }
        } catch (IOException e) {
            AutoBackupMod.LOGGER.warn("保存配置文件失败: {}", e.getMessage());
        }
    }

    private void validate() {
        intervalMinutes = Math.max(0, Math.min(intervalMinutes, 24 * 60));
        keepBackups = Math.max(1, Math.min(keepBackups, 1000));
        compressLevel = Math.max(0, Math.min(compressLevel, 9));
        if (upload == null) {
            upload = new Upload();
        }
        upload.url = upload.url == null ? "" : upload.url.trim();
        upload.token = upload.token == null ? "" : upload.token;
        upload.username = upload.username == null ? "" : upload.username.trim();
        upload.game = (upload.game == null || upload.game.isBlank()) ? "minecraft" : upload.game.trim();
        upload.fieldName = (upload.fieldName == null || upload.fieldName.isBlank())
                ? "file" : upload.fieldName.trim();
        upload.timeoutSeconds = Math.max(10, Math.min(upload.timeoutSeconds, 3600));

        if (schedule == null) {
            schedule = new Schedule();
        }
        if (schedule.slots == null) {
            schedule.slots = new ArrayList<>();
        }
        schedule.slots.removeIf(slot -> slot == null);

        if (retention == null) {
            retention = new Retention();
        }
        retention.latest = Math.max(1, Math.min(retention.latest, 50));
        if (retention.days == null) {
            retention.days = new ArrayList<>();
        }
        retention.days.removeIf(day -> day == null || day < 0 || day > 365);
    }

    public static Path path() {
        return CONFIG_PATH;
    }
}
