// UELink 的版本号：和 UELink.uplugin 的 VersionName、CHANGELOG.md 最上面一节保持一致（scripts/check.mjs 会核对）。
// 名片、ping 回复里报的都是它，面板和 UEToVS 拿它判断要不要提示更新。
#pragma once

#define UELINK_VERSION TEXT("2.0.0")

/**
 * 推送通道（TCP 一行一个 JSON）的协议号。沿用 UEToVSLink 的协议 2：UEToVS 0.4.5 起的客户端按它判断能不能订阅，
 * 只有线上格式不兼容时才加，加功能只往 features 里加名字。
 */
#define UELINK_STREAM_PROTOCOL 2
