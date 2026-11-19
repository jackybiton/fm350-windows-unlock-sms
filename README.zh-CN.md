# FM350 for Windows：FCC 解锁与短信

[English](README.md)

在非 Dell 主板上为 Dell DW5931E（Fibocom FM350-GL）解除启动后的 FCC 软件锁，并配置 Windows 开机自动解锁；另附可选的短信收发应用（见“短信应用”一节）。

本项目不会修改或刷写模组固件，也不包含 Dell 驱动、认证程序或其他专有二进制文件。

## 已验证环境

- 模组：Dell DW5931E / Fibocom FM350-GL
- PCI ID：`14C3:4D75`，Dell 子系统 `1028:5931`
- 固件：`81600.0000.00.29.23.28_CT / F32`
- 系统：Windows 10 IoT Enterprise LTSC 19044 x64
- 主板：非 Dell X99 主板
- 网络：中国电信，5G SA

该环境已验证 FCC 认证、开机自动解锁和中国电信 5G SA 数据连接。样机随后在高负载下还出现了与 FCC 认证无关的 PCIe/WHEA 及 NVMe 掉盘；移除 FM350 后仍能复现存储设备异常，因此不将该样机的长期硬件稳定性列为已验证。其他主板、转接板、固件、运营商和 OEM 版本尚未验证。完整边界与版本变更见 [CHANGELOG.md](CHANGELOG.md)。

## 问题表现

驱动、SIM、IMEI 和 ICCID 均可正常识别，但 Windows 显示：

```text
Hardware radio state : Off
Software radio state : On
```

Dell 的 `WWAN ModemAuthenticator` 服务会检查 Dell SMBIOS。在普通台式机主板上，日志可能出现：

```text
CheckDellPcIdentifyRegistry return false
Can not find type: 1 in SMBIOS table
DoFccUnlock: Not find SMBIOS FCC Type
```

## 快速安装

先安装适合该模组的 Dell 驱动。然后以管理员身份打开 Windows PowerShell：

```powershell
git clone https://github.com/jackybiton/fm350-windows-unlock-sms.git
cd fm350-windows-unlock-sms
Set-ExecutionPolicy -Scope Process Bypass
.\Install.ps1
```

安装程序会：

1. 构建自包含的 x64 .NET Windows 服务，并备份匹配网卡实例及 Dell 认证服务的原始状态。
2. 保留 PCIe COM AT 诊断端口；对网卡及其 COM AT 子设备关闭选择性挂起、断开后设备休眠、PCI D3cold 和 WDF 工作状态空闲转换，降低不兼容电源转换导致 PCIe Surprise Down、设备电源故障及 Code 10 的风险。这些设置不能修复主板、转接板或供电链路的电气不稳定。
3. 停止并禁用在非 Dell SMBIOS 上无法工作的原认证服务。
4. 安装以 `LocalSystem` 自动启动的 `FM350UnlockService`。服务通过 WinRT 监听无线状态和 PCIe 重枚举。
5. 无线处于 FCC 锁定状态时立即通过已验证的私有 MBIM 命令完成认证；无线已经打开时只监控，不重复认证。
6. 删除旧版本留下的 AT 开机任务。正式服务不依赖 COM 端口或物理 USB 线，但 AT 端口仍可供手工频段/小区诊断使用。

冷启动时，样机可能以 Fibocom 通用人格 `SUBSYS_35001CF8` 或 Dell 运行人格 `SUBSYS_59311028` 出现。前者在锁定时不响应前置 Query，因此服务使用 Set → Set → post-Query；后者使用原厂 Query → Set → Set → post-Query。两条路径均以 post-Query 第二字段变为 `1` 作为成功判据，不会在一次认证中混用 AT。

从源码安装需要 .NET SDK 10；安装后的程序为自包含单文件，不依赖系统预装 .NET 运行时。

日志位置：

```text
C:\ProgramData\FM350Unlock\service.log
```

## 手动运行与验证

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Unlock-FM350.ps1
.\src\Fm350Unlock.Service\bin\Release\net10.0-windows10.0.26100.0\win-x64\Fm350UnlockService.exe --self-test
.\src\Fm350Unlock.Service\bin\Release\net10.0-windows10.0.26100.0\win-x64\Fm350UnlockService.exe --probe-mbim
.\src\Fm350Unlock.Service\bin\Release\net10.0-windows10.0.26100.0\win-x64\Fm350UnlockService.exe --query-mbim-default
netsh mbn show radio interface=*
netsh mbn show connection interface=*
```

成功时 `service.log` 会记录 MBIM challenge、两次 Set 响应和认证后的 Query 响应，随后 Windows 的硬件无线状态变为 `On`。

无需连接模组即可运行算法自测：

```powershell
.\Unlock-FM350.ps1 -SelfTest
```

## MBIM 实机验证

原认证程序使用 `MobileBroadbandDeviceService`，服务 GUID 为 `{F85D46EF-AB26-4081-9868-4D183C0A3AEC}`，FCC Query 和两次 Set 都使用命令 `1`。样机冷启动实测已确认纯 MBIM 流程可以完成解锁并连接中国电信 5G SA。该固件的验证 Set 响应可能为全零，因此服务会在 Set 状态码为零后再次 Query，并以第二字段从 `0` 变为 `1` 作为最终成功判据。命令 `2` 不属于 FCC 流程，不会调用。详情见 [docs/MBIM.md](docs/MBIM.md)。

## 解锁算法

Dell 认证程序在 Dell SMBIOS OEM 结构不可用时无法取得 FCC 种子。DW5931E 使用的种子为 ASCII 字符串：

```text
DW5931EFCCLOCK
```

MBIM 与备用 AT 工具共用同一个哈希应答：

1. 取得模组返回的 4 字节 challenge。MBIM 从第一次 Set 响应的偏移 4 读取；AT 工具通过 `AT+GTFCCLOCKGEN` 取得。
2. 计算 `SHA-256("DW5931EFCCLOCK")`，取 digest 前 4 字节，得到 `49 09 b5 a4`。
3. 将 challenge 的 4 个原始字节与上述 4 字节连接，再计算 SHA-256。
4. MBIM 验证载荷为小端 `DWORD 1` 加新 digest 的前 4 个原始字节；AT 工具则将这 4 字节解释为大端无符号整数，以十进制发送 `AT+GTFCCLOCKVER=<response>`。

这与 Lenovo FM350-GL 使用的厂商哈希不同，不能直接套用 Lenovo 的 `3df8c719`。

完整的诊断、逆向定位与验证过程见 [docs/RESEARCH.md](docs/RESEARCH.md)。文档中的设备身份、SIM 信息、主机名和公网/地址标识均已删除。

## 短信应用（可选）

`Fm350Sms` 是一个用于该模组的希伯来文（从右到左）短信应用。它通过 Windows 移动宽带 API（`IMbnSms`）经 MBIM 收发短信，不需要 COM AT 端口，也不需要管理员权限。分段长短信会自动合并，支持 GSM 7 位、8 位和 UCS2（中文、希伯来文等）编码。

- **窗口与托盘。** 会话按最新消息排序，搜索框可按姓名、号码和内容过滤。关闭窗口后应用继续在通知区域运行。每条新短信都会显示 Windows 通知，点击通知即打开对应会话。
- **联系人。** “אנשי קשר → ייבוא”（联系人 → 导入）读取 Google 通讯录导出的 CSV（或 vCard `.vcf`）并为会话命名，再次导入会替换之前导入的联系人。在应用中设置的名称（重命名、新建联系人）在重新导入后仍保留，“ייצוא”（导出）会把它们写成 Google 通讯录可以导入的 Google CSV 文件。
- **删除。** 右键单击一条消息或一个会话即可删除（需确认）。只从应用中删除，绝不会从模组删除，被删除的短信也不会再次出现。
- **发送。** 点击“הודעה חדשה”（新消息），或在已打开的会话中输入。Enter 发送，Shift+Enter 换行。长短信会分段发送。“נשלחה” 表示模组已接受该短信；“נשלחה (המודם לא אישר)” 表示短信已交给模组，但模组没有确认，请先向对方确认，再决定是否重发。发送失败的短信可以重试；如果模组没有应答或只发出了部分分段，消息会注明这一点，“שלח שוב”（再次发送）会先询问，因为对方可能已经收到。一条短信最多 10 段。
- **存储。** 短信、联系人和设置保存在 `%LOCALAPPDATA%\FM350Sms\sms.db`（SQLite）和 `settings.json` 中。v0.1.0 保存的短信（`messages.txt`）会导入一次。

以当前用户安装（需要 .NET SDK 10）：

```powershell
.\Install-Sms.ps1                   # 保存后短信仍保留在模组/SIM 中
.\Install-Sms.ps1 -DeleteAfterSave  # 保存后从模组删除短信（写入 settings.json）
```

> **请在普通 PowerShell 窗口中运行安装程序**（开始菜单 → PowerShell）。Claude 桌面应用等打包（MSIX）应用会把其启动的所有进程的 `%LOCALAPPDATA%` 重定向到私有副本（`%LOCALAPPDATA%\Packages\<应用>\LocalCache\Local`），在那里安装或启动的应用会使用另一个独立的数据库。安装程序和应用会检测到这种情况并停止。如果之前的运行在 Claude 副本中留下了历史记录，安装程序会询问是否将其合并到真正的数据库（合并前会先备份）。

应用会在每次登录时最小化启动（HKCU Run 键）。可从开始菜单（“FM350 SMS”）或双击托盘图标打开窗口。`-DeleteAfterSave` 保存在 `settings.json` 中，之后在应用菜单中关闭会一直生效；不带该开关再次运行安装程序会保留当前设置。如果安装程序旁有 `contacts` 文件夹，导入对话框会默认打开该文件夹。常用命令：

```powershell
$sms = "$env:LOCALAPPDATA\FM350Sms\bin\Fm350Sms.exe"
& $sms --where | Out-String               # 当前使用的数据文件夹，以及是否被重定向
& $sms --list | Out-String                # 模组中存储的短信
& $sms --test-notification
& $sms --merge-db C:\path\to\sms.db | Out-String   # 合并另一个数据库（先备份，可重复运行）
```

运行 `.\Uninstall-Sms.ps1` 卸载（同样在普通 PowerShell 窗口中运行）；除非指定 `-RemoveMessages`，短信和联系人会保留。暂不支持 class 0（闪信）。

## 卸载

以管理员身份运行：

```powershell
.\Uninstall.ps1
```

卸载程序会删除服务和旧版遗留任务，恢复安装前记录的网卡及设备电源注册表值和 Dell 服务启动类型。日志与回滚记录会保留在 `C:\ProgramData\FM350Unlock` 供审计使用。

## 故障排查

- 找不到 COM AT 端口：不影响正式服务；0.4.1 的自动解锁不访问 COM 端口。若要做手工频段/小区诊断，请确认匹配网卡实例的 `WWANFeatureAT=1` 并在重启后检查设备管理器。
- 仍显示硬件无线关闭：查看 `service.log` 中 Query、challenge、verify 和 postVerify 字段。`SUBSYS_35001CF8` 的 Query 显示 `<skipped>` 是正常的；成功的 postVerify 通常为 `0200000001000000`。
- SIM 已识别但未注册：检查天线、APN、运营商频段和套餐；这已经不是 FCC 锁问题。
- USB 连接：正式服务走 PCIe/MBIM 驱动路径，不依赖主板上的物理 USB 线，可以把该 USB 接口留给蓝牙。
- 高负载时设备管理器中的网卡消失、出现 Code 10、WHEA `Surprise Down`，或其他 PCIe/NVMe 设备同时掉线：应转入 PCIe 链路或供电稳定性排查，这不是典型的 FCC 应答值错误。请优先检查带电容的合规转接板、插槽供电与信号质量、BIOS PCIe 设置及主板稳定性。

## 安全与合规

仅在你拥有或获授权维护的设备上使用。使用蜂窝射频设备时，应遵守所在地的无线电法规、运营商规则及硬件认证要求。项目按 MIT 许可证提供，不附带任何担保。

## 致谢

- 基于 [AsterZ99/fm350-dell-fcc-unlock-windows](https://github.com/AsterZ99/fm350-dell-fcc-unlock-windows)（FCC 解锁）；本分支增加了短信应用。
- ModemManager 社区公开的 FM350-GL FCC challenge/response 工作为协议分析提供了参考。
- Fibocom、Dell 和 Intel/MediaTek 保留各自产品及商标权利；本项目与这些厂商无隶属关系。
