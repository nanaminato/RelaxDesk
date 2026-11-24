# Independent EXE installer acceptance

Delivered files are under `build/RelaxDesk-exe-installers-win-x64-20261124-final`; each has an adjacent `.sha256`. [Artifact/provenance JSON](2026-11-24-exe-installers-artifacts.json), [unmodified final lifecycle result](2026-11-24-exe-installers-result.json), [operator/build guide](../docs/EXE-INSTALLERS.md).

| Installer | Bytes | SHA-256 |
| --- | ---: | --- |
| `RelaxDesk-Host-Setup-x64.exe` | 107,291,166 | `17B940445621A84F19A7F502F717670EF4C13FBFF471668999DC6A084B9A9BFC` |
| `RelaxDesk-Viewer-Setup-x64.exe` | 50,654,619 | `A209EBD0860715108E45C29D732F7C14B4D99A5C6F86FFC661B8A72198204EB1` |

Installer recipe revision **`fff30fadc361ad8fb94c654022c3d022fdc2c1a2`**. Application payloads are the exact previously verified role archives, clean app revision `7e51e9549819c9d088bae7cacbc4e7ed4fd4740b`, version `0.4.2-roles.20261124-202127.g7e51e95`. No application, codec, protocol or authorization change. Native ABI/runtime closure remains the original stable .NET 11 self-contained x64 payload. Later commits add verification/docs and clarify compiler-version metadata only, not installer bytes.

## Build dependency and scope

Official stable Inno Setup **7.1.1** x64, not its 7.2 preview, is used as an external compiler. The downloaded official bootstrapper has valid Pyrsys B.V. Authenticode signature and SHA-256 `8A5A6711C3766A6E5FB74F280BA0E3551E719CE94538D901FDBD1F8F9920B3F0`. Official `/PORTABLE=1 /CURRENTUSER` expands the tool into workspace build without tool registration/shortcuts. Compiler ISCC itself also has a valid publisher signature, SHA-256 `E0F9F1DC32032650C6629F6DEE41EB6D20BA140E4E3C07C25D8A24F9BA781644`. Its `--version` returns 7.1.1; the PE ProductVersion resource is a placeholder 0.0.0.0, so artifact metadata records both explicitly after clarification. Generated RelaxDesk EXEs are **NotSigned**; the compiler's signature does not sign the resulting product.

Compiler licensing/copyright is preserved verbatim as a third-party notice in each installed receipt directory; no GPL source is copied and no project license is selected. Recipe, publisher acquisition/verification links and dependencies are described in the operator/build guide. Original proof builds and output-off syntax checks are retained under `build/exe-installer-*.txt`; final accepted compile log is `build/exe-installers-final-build.txt`. No output/package is overwritten.

## Observed real installation, not mock setup

The **actual final EXEs** are installed using their normal silent parameters into fresh repository/build directories with Unicode, spaces and ampersands. They create real current-user Apps entries, Start Menu and desktop shortcuts. Tests first refuse any pre-existing same-role registration or same-named shortcut; none existed. No fake SCM/shortcut implementation is used for this lifecycle. No system service is installed or started, no firewall/startup policy changes are made, and no administrator token is requested by ordinary app setup.

Initial lifecycle: **38 checks PASS**, terminal exit 0 (`build/exe-installer-lifecycle-20261124.txt`). An expanded run verifies **both** normally launched installed applications and installed Host helper resolution: **46 checks / 15 executable steps PASS**, terminal exit **0**, `PASS_ACTUAL_PER_USER_INSTALLER_LIFECYCLE`. Raw final log is `build/exe-installer-accepted-lifecycle-20261124.txt`, detailed native installer logs and result are under `build/exe-installer-accepted-lifecycle-20261124`.

Verified behavior:

- independent install, Apps identity/version/location, direct-EXE desktop and Start Menu links and exact full installed payload hashes;
- normal installed HostShell and Viewer WPF windows open; HostShell does not implicitly launch a sharing Host child;
- installed Host Service executable accepts readonly package preflight from its actual versioned installation directory, no SCM mutation;
- repeated same-version installation is idempotent without duplicate receipt state;
- unrelated nonempty directory, junction target and attempt to take over another install location are rejected before adoption;
- modified payload blocks both overwrite and uninstall, retaining the actual edit and registration;
- running Host/Viewer block updates; running Viewer blocks its uninstall without termination;
- uninstalling Host while Viewer runs leaves Viewer and its integration intact;
- unknown Host file is retained; clean Viewer payload/integration is removed;
- test-owned app registry entries, role receipt keys and desktop/Start Menu links are removed after test. Unknown fixture data/logs remain in workspace for evidence.

The receipt preflight uses SHA-256, path/ancestor reparse rejection with extended native paths, bounded file enumeration and owned shortcut validation. No recursive wildcard deletion or force-kill is used. Modified metadata/files cause refusal rather than silently deleting user changes. Cross-version upgrade, eight-version cap, hostile concurrent mutation, inaccessible/redirected known folders, power loss and antivirus/file-lock failures are not claimed validated by same-version reinstall tests.

## Remaining operator gates

This runner is the existing Windows development environment, not a clean physical Home machine. Ordinary wizard mouse interaction, default LocalAppData install destination, Finish-page automatic launch and Windows Apps UI presentation have configured standard framework behavior but were not manually clicked/visually certified. Actual desktop shortcut target startup and Apps registry/real uninstall behavior were exercised as above. The test launch opens ordinary initial windows and does not type or control a remote physical desktop.

Windows Home dual-PC input/IME/clipboard/audio, display/driver/DPI/HDR, actual service UAC/admin/SCM deployment/boot and long office soak remain pending. The historical 23 split-package assertions and 54 one-click UI tests remain separate; none are relabeled as physical or service deployment success. These unsigned experimental installers do not bypass SmartScreen, security policy or execution policy. No public release/push/merge or owner license decision was made.
