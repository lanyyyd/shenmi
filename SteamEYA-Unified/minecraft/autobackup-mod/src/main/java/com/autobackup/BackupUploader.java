package com.autobackup;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.net.http.HttpRequest.BodyPublishers;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.UUID;

public final class BackupUploader {
    private static final HttpClient CLIENT = HttpClient.newBuilder()
            .connectTimeout(Duration.ofSeconds(15))
            .followRedirects(HttpClient.Redirect.NORMAL)
            .build();

    public record Result(boolean success, String message) {
    }

    /** 随 zip 一起上传的整合包 / 存档信息。 */
    public record Metadata(
            String versionName, String gameVersion, String saveName,
            String saveFolder, String savePath, long savedAt) {
    }

    private BackupUploader() {
    }

    public static Result upload(BackupConfig.Upload config, Path file) {
        return upload(config, file, null);
    }

    public static Result upload(BackupConfig.Upload config, Path file, Metadata meta) {
        if (config == null || config.url == null || config.url.isBlank()) {
            return new Result(false, "未配置上传地址");
        }
        if (!Files.isRegularFile(file)) {
            return new Result(false, "备份文件不存在");
        }

        String fileName = file.getFileName().toString();
        // multipart 里的 filename 只用 ASCII，避免服务端解析器对 UTF-8 文件名的兼容问题；
        // 中文存档名由 save_name 字段单独上报。
        String uploadFileName = asciiFileName(fileName);
        String boundary = "----AutoBackup" + UUID.randomUUID().toString().replace("-", "");
        String fieldName = config.fieldName == null || config.fieldName.isBlank() ? "file" : config.fieldName;

        HttpRequest request;
        try {
            StringBuilder headText = new StringBuilder();
            appendField(headText, boundary, "username", config.username);
            appendField(headText, boundary, "game", config.game);
            if (meta != null) {
                appendField(headText, boundary, "version_name", meta.versionName());
                appendField(headText, boundary, "game_version", meta.gameVersion());
                appendField(headText, boundary, "save_name", meta.saveName());
                appendField(headText, boundary, "save_folder", meta.saveFolder());
                appendField(headText, boundary, "save_path", meta.savePath());
                appendField(headText, boundary, "saved_at", String.valueOf(meta.savedAt()));
            }
            headText.append("--").append(boundary).append("\r\n")
                    .append("Content-Disposition: form-data; name=\"").append(fieldName)
                    .append("\"; filename=\"").append(uploadFileName).append("\"\r\n")
                    .append("Content-Type: application/zip\r\n\r\n");

            byte[] head = headText.toString().getBytes(StandardCharsets.UTF_8);
            byte[] tail = ("\r\n--" + boundary + "--\r\n").getBytes(StandardCharsets.UTF_8);

            HttpRequest.Builder builder = HttpRequest.newBuilder(URI.create(config.url))
                    .timeout(Duration.ofSeconds(Math.max(10, config.timeoutSeconds)))
                    .header("Content-Type", "multipart/form-data; boundary=" + boundary)
                    // 请求头只能是 ASCII：中文存档名放 multipart 正文里，这里不带头文件名。
                    .header("User-Agent", "AutoBackup/1.1")
                    .POST(BodyPublishers.concat(
                            BodyPublishers.ofByteArray(head),
                            BodyPublishers.ofFile(file),
                            BodyPublishers.ofByteArray(tail)));
            if (config.token != null && !config.token.isBlank()) {
                builder.header("Authorization", "Bearer " + config.token);
            }
            request = builder.build();
        } catch (Exception e) {
            return new Result(false,
                    "构造上传请求失败：" + e.getClass().getSimpleName() + ": " + e.getMessage());
        }

        String lastError = "未知错误";
        for (int attempt = 1; attempt <= 3; attempt++) {
            try {
                HttpResponse<String> response = CLIENT.send(request, HttpResponse.BodyHandlers.ofString());
                int code = response.statusCode();
                if (code >= 200 && code < 300) {
                    return new Result(true, "HTTP " + code);
                }
                lastError = "HTTP " + code
                        + (response.body().isBlank() ? "" : "：" + truncate(response.body()));
                if (code < 500) {
                    break;
                }
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                return new Result(false, "上传被中断");
            } catch (Exception e) {
                lastError = e.getClass().getSimpleName() + ": " + e.getMessage();
            }
            if (attempt < 3) {
                try {
                    Thread.sleep(2000L * attempt);
                } catch (InterruptedException e) {
                    Thread.currentThread().interrupt();
                    return new Result(false, "上传被中断");
                }
            }
        }
        return new Result(false, lastError);
    }

    private static void appendField(StringBuilder out, String boundary, String name, String value) {
        out.append("--").append(boundary).append("\r\n")
                .append("Content-Disposition: form-data; name=\"").append(name).append("\"\r\n\r\n")
                .append(sanitize(value == null ? "" : value)).append("\r\n");
    }

    private static String sanitize(String value) {
        return value.replace('"', '_').replace('\r', '_').replace('\n', '_');
    }

    /** 把文件名转成纯 ASCII（保留 .zip 后缀），并去掉会破坏 multipart 的字符。 */
    private static String asciiFileName(String value) {
        StringBuilder out = new StringBuilder(value.length());
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            out.append(c >= 0x20 && c < 0x7F && c != '"' && c != '\\' ? c : '_');
        }
        String name = out.toString().trim();
        return name.isEmpty() ? "backup.zip" : name;
    }

    private static String truncate(String text) {
        String oneLine = text.replaceAll("\\s+", " ").trim();
        return oneLine.length() <= 160 ? oneLine : oneLine.substring(0, 160) + "...";
    }
}
