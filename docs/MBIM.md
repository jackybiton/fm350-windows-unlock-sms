# FCC MBIM device-service research

Dell's signed `WWANModemAuthenticator.exe` uses the WinRT `MobileBroadbandDeviceService` API. Static inspection and read-only probing on the verified DW5931E identified this service:

```text
Device service GUID: F85D46EF-AB26-4081-9868-4D183C0A3AEC
Supported commands: 1, 2
```

The service binary names its operations `Query_INTEL_FCC_MBIM_Extension` and `Set_INTEL_FCC_MBIM_Extension`.

The static reference used for this analysis is Dell authenticator version `5.0.15.0`, SHA-256 `349AF0A9AD5A16BDF5365C3E195070C9201EDC87E91500B769D135DB0E7C2F25`. The proprietary binary is not included in this repository.

## Safe observations

- Query command 1 with an empty input buffer completed successfully while the radio was already unlocked.
- The response is eight bytes containing two little-endian 32-bit fields.
- Before FCC authentication it was `02 00 00 00 00 00 00 00` (`2`, `0`).
- After FCC authentication it was `02 00 00 00 01 00 00 00` (`2`, `1`).
- Static analysis of Dell's signed service shows that both FCC Query and FCC Set use command 1. The helper's operation selector is `1` for Query and `0` for Set.
- Dell's sequence always performs Query command 1 first and reads byte 0 (`lm`) plus byte 4 (`ls`). It proceeds only when `lm != 0` and `ls == 0`.
- The first Set request is eight zero bytes. Its response supplies the four-byte challenge at offset 4.
- The second Set request is eight bytes: little-endian `DWORD 1`, followed by the four raw response-digest bytes.
- Some firmware returns an all-zero verification response even when the Set succeeds. A zero Set status followed by a Query whose byte 4 is non-zero is the authoritative success check.
- Command 2 is not used by the FCC path. An earlier read-only probe showed that Query command 2 does not complete, so the project neither queries nor writes it.

The first field remained `2`; the second field tracks FCC state (`0` locked, `1` unlocked). Command 1 is therefore a status query and does not expose the challenge required by the AT flow.

## Hardware validation

An early 0.3.1 experiment omitted Dell's leading status Query and incorrectly treated an all-zero verification response as rejection. Mixing an AT exchange into that incomplete attempt also produced an abnormal zero challenge and coincided with PCIe resets, so mixed-protocol automation was removed.

The complete Dell-compatible sequence was subsequently validated in a `LocalSystem` cold-start test with AT automation disabled:

```text
queryResponse     = 0200000000000000
challenge         = 0F457CA8
challengeResponse = 010000000F457CA8
verifyRequest     = 01000000F555B06B
verifyStatus      = 0
verifyResponse    = 0000000000000000
postVerify        = 0200000001000000
```

The hardware radio changed to `On`, and Windows registered and connected on China Telecom 5G SA. Version 0.4.0 therefore uses this MBIM-only path in the production service. Each command obtains `MobileBroadbandModem.GetDefault()`, opens a fresh device-service session, and uses command 1. Set buffers have capacity 4096 and logical length 8; Query uses capacity 4096 and logical length 0. Command 2 and the COM AT port are not used for automatic authentication; COM AT may remain enabled for manual radio diagnostics.

On the verified hardware, different cold boots exposed either `PCI\VEN_14C3&DEV_4D75&SUBSYS_35001CF8` or Dell's `SUBSYS_59311028`. While the generic persona was FCC-locked, Query command 1 timed out even after several minutes of device uptime. An AT unlock on that same device made Query immediately return `02 00 00 00 01 00 00 00`, without a PCIe reset or persona change. This proves that the private endpoint exists but its Query path is unavailable while that persona is locked.

Earlier Set-first experiments on the generic persona had already produced a valid challenge response and a zero-status, all-zero verification response. The all-zero response was originally misclassified as rejection, and the following AT attempt mixed protocols unnecessarily. Version 0.4.0 instead uses Set → Set → post-Query for `SUBSYS_35001CF8`, and Query → Set → Set → post-Query for `SUBSYS_59311028`. Automatic AT fallback remains disabled; COM AT is retained only for manual recovery and radio diagnostics.
