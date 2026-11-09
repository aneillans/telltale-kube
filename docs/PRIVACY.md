# Telltale Kube Privacy Policy

Effective date: [DATE OF PUBLICATION]

Telltale Kube is a Kubernetes client for Windows, macOS, Linux, Android and iPhone. It is published by Andy Neillans ("I"). This policy covers the app and explains what data it handles.

I do not collect, receive, store or sell your data. The app has no accounts, telemetry, analytics, advertising or crash reporting, and I run no servers that it connects to.

## Data stored on your device

The app stores the following on your device so that it can work:

- Kubeconfigs and cluster connection details, including server addresses, tokens, client certificates and keys.
- App settings and preferences.

This data is held in an encrypted local vault (AES-256-GCM). The vault's key is protected by your device's secret store: Android Keystore, iOS or macOS Keychain, Windows DPAPI or Linux Secret Service. On a Linux system with no Secret Service available, the key is protected only by file permissions.

On Android, the vault is excluded from Google cloud backup and device-to-device transfer.

Removing the app's data or uninstalling the app deletes this data from your device, subject to how your operating system handles uninstalls. Desktop operating systems may leave the app's data folder in place until you delete it.

## Data sent over the network

The app makes network connections in these cases:

- **Your Kubernetes clusters.** The app connects to the clusters you add, using the credentials you supply, and shows you data from those clusters. This traffic goes directly between your device and your cluster. I never see it. Whoever operates your cluster controls how that traffic and any related logs are handled.
- **Credential plugins (desktop only).** If your kubeconfig uses an exec credential plugin and you approve it, that program runs on your computer and may contact its own provider, such as your cloud vendor. That provider's privacy terms apply.
- **Update checks (desktop only).** Desktop builds check GitHub for new releases at startup and when you select "Check now". This is on by default and you can turn off the startup check in Settings. GitHub receives the request, including your IP address, under [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement). Android and iPhone builds make no update check and receive updates through their app store.

## Device permissions

On Android, the app requests:

- Internet access, to connect to your clusters.
- Biometric authentication, to unlock the app and reveal secrets. The operating system handles your biometric data, and the app only receives a pass or fail result.

## Clipboard

You can paste a kubeconfig from the clipboard, and the app reads the clipboard only when you ask it to. Secrets you copy from the app are cleared from the clipboard after 45 seconds.

## App stores

If you install the app through Google Play or the Apple App Store, that store may collect data about your installation and use under its own privacy terms. Stores may give me aggregated statistics and diagnostics, such as install counts and crash rates, through their developer consoles. I do not use these to identify you.

If you download a release from GitHub, GitHub's privacy statement applies to that download.

## Sharing

I do not share your data with anyone, because I do not have it.

## Children

The app is a tool for managing Kubernetes clusters and is not directed at children.

## Your rights

Your data stays on your device and under your control, so you can view, change or delete it in the app. I hold no personal data from the app to access, correct or delete. If you contact me, I will use your message and contact details only to reply.

## Changes

Changes to this policy will be published at this page, with a new effective date. Its history is available in the project's public repository.

## Contact

[PRIVACY CONTACT EMAIL]
