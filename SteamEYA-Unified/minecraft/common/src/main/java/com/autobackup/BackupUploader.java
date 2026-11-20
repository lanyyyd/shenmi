package com.autobackup;

import java.io.BufferedOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.UUID;

public final class BackupUploader {
    /** 上传进度回调：已发送字节 / 总字节。 */
    public interface ProgressListener {
        void onProgress(long sentBytes, long totalBytes);
    }

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
        return upload(config, file, null, null);
    }

    public static Result upload(BackupConfig.Upload config, Path file, Metadata meta) {
        return upload(config, file, meta, null);
    }

    public static Result upload(BackupConfig.Upload config, Path file, Metadata meta,
                                ProgressListener listener) {
        if (config == null || config.url == null || config.url.isBlank()) {
            return new Result(false, "未配置上传地址");
        }
        if (!Files.isRegularFile(file)) {
            return new Result(false, "备份文件不存在");
        }

        byte[] head;
        byte[] tail;
        long fileSize;
        String boundary = "----AutoBackup" + UUID.randomUUID().toString().replace("-", "");
        try {
            String fileName = file.getFileName().toString();
            // multipart 里的 filename 只用 ASCII，中文存档名由 save_name 字段单独上报。
            String uploadFileName = asciiFileName(fileName);
            String fieldName = config.fieldName == null || config.fieldName.isBlank()
                    ? "file" : config.fieldName;

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

            head = headText.toString().getBytes(StandardCharsets.UTF_8);
            tail = ("\r\n--" + boundary + "--\r\n").getBytes(StandardCharsets.UTF_8);
            fileSize = Files.size(file);
        } catch (Exception e) {
            return new Result(false,
                    "构造上传请求失败：" + e.getClass().getSimpleName() + ": " + e.getMessage());
        }

        long total = head.length + fileSize + tail.length;
        String lastError = "未知错误";
        for (int attempt = 1; attempt <= 3; attempt++) {
            try {
                Result result = sendOnce(config, file, boundary, head, tail, total, listener);
                if (result.success()) {
                    return result;
                }
                lastError = result.message();
                // 4xx 是请求或鉴权问题，重试无意义
                if (lastError.startsWith("HTTP 4")) {
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

    private static Result sendOnce(BackupConfig.Upload config, Path file, String boundary,
                                   byte[] head, byte[] tail, long total,
                                   ProgressListener listener) throws IOException, InterruptedException {
        HttpURLConnection connection = (HttpURLConnection) URI.create(config.url).toURL().openConnection();
        connection.setRequestMethod("POST");
        connection.setConnectTimeout(15_000);
        connection.setReadTimeout(Math.max(10, config.timeoutSeconds) * 1000);
        connection.setDoOutput(true);
        // 固定长度流式上传：不整包读入内存，且能精确计算进度与速度。
        connection.setFixedLengthStreamingMode(total);
        connection.setRequestProperty("Content-Type", "multipart/form-data; boundary=" + boundary);
        connection.setRequestProperty("User-Agent", "AutoBackup/1.1");
        if (config.token != null && !config.token.isBlank()) {
            connection.setRequestProperty("Authorization", "Bearer " + config.token);
        }

        long sent = 0;
        try (OutputStream out = new BufferedOutputStream(connection.getOutputStream(), 64 * 1024)) {
            out.write(head);
            sent += head.length;
            notifyProgress(listener, sent, total);

            byte[] buffer = new byte[64 * 1024];
            try (InputStream in = Files.newInputStream(file)) {
                int read;
                while ((read = in.read(buffer)) > 0) {
                    if (Thread.currentThread().isInterrupted()) {
                        throw new InterruptedException();
                    }
                    out.write(buffer, 0, read);
                    sent += read;
                    notifyProgress(listener, sent, total);
                }
            }
            out.write(tail);
            sent += tail.length;
            notifyProgress(listener, sent, total);
        }

        int code = connection.getResponseCode();
        InputStream stream = code >= 400 ? connection.getErrorStream() : connection.getInputStream();
        String body = "";
        if (stream != null) {
            try (InputStream in = stream) {
                body = new String(in.readAllBytes(), StandardCharsets.UTF_8);
            }
        }
        if (code >= 200 && code < 300) {
            return new Result(true, "HTTP " + code);
        }
        return new Result(false, "HTTP " + code
                + (body.isBlank() ? "" : "：" + truncate(body)));
    }

    private static void notifyProgress(ProgressListener listener, long sent, long total) {
        if (listener == null) {
            return;
        }
        try {
            listener.onProgress(sent, total);
        } catch (Throwable ignored) {
            // 进度回报不能影响上传本身
        }
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
