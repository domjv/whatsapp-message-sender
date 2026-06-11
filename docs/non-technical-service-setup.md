# Non-Technical Setup Guide: One WhatsApp Number per ERP Instance

This guide is for the person who needs to set up or operate the WhatsApp sender
services. It avoids code-level details and focuses on safe steps.

## What we are setting up

Each ERP instance gets its own WhatsApp sender service.

Example:

| ERP | Service name | WhatsApp number | Chrome profile folder |
| --- | --- | --- | --- |
| AJK | `whatsapp-sender-ajk` | AJK WhatsApp number | `/var/lib/whatsapp-sender/ajk/chrome-profile` |
| Ivy Living | `whatsapp-sender-ivyliving` | Ivy Living WhatsApp number | `/var/lib/whatsapp-sender/ivyliving/chrome-profile` |
| St Thomas | `whatsapp-sender-stthomas` | St Thomas WhatsApp number | `/var/lib/whatsapp-sender/stthomas/chrome-profile` |

The important rule is simple: **one service, one Chrome profile folder, one
WhatsApp number, one ERP instance**.

## Why separate services are used

Separate services are easier to operate safely:

- if one WhatsApp number logs out, the other numbers can continue;
- if one ERP's sender needs restart, the others do not need restart;
- each Chrome profile is kept separate;
- there is less chance of sending from the wrong WhatsApp number;
- each service has its own logs.

## Before you start

Ask the technical/admin person to give you these details for each ERP instance:

- ERP name, for example `ajk`;
- service name, for example `whatsapp-sender-ajk`;
- Chrome profile folder;
- WhatsApp phone number to link;
- confirmation that the Service Bus topics or Redis stream are only for that ERP;
- server login details;
- command to start/stop/check the service.

## First-time WhatsApp login

1. Start only one sender service.
2. A Chrome window should open WhatsApp Web.
3. Use the correct phone for that ERP instance.
4. Open WhatsApp on the phone.
5. Go to linked devices.
6. Scan the QR code shown in Chrome.
7. Wait until WhatsApp Web finishes loading.
8. Check the service log for this message:

```text
WhatsApp Web is logged in — the worker will now connect to your message broker.
```

Do not start the next ERP service until this one is logged in correctly.

## Daily checks

For each service, check:

- the service is running;
- WhatsApp Web did not log out;
- messages are being sent;
- the server is not low on memory;
- the disk is not full.

## If messages stop sending

Use this order:

1. Check whether the service is running.
2. Check whether Chrome/WhatsApp Web is logged in.
3. If WhatsApp shows a QR code, scan it again with the correct phone.
4. Restart only the affected service.
5. Send one test message.
6. If it still fails, contact technical support with the service name and latest logs.

## Very important warnings

### Never share Chrome profile folders

Do not configure two services with the same Chrome profile folder. This can break
the browser profile or stop the sender from starting.

The app now checks this and should show an error if the profile is already in use.

### Never mix ERP topics/streams

The AJK service must only receive AJK messages. The Ivy Living service must only
receive Ivy Living messages.

If the wrong messages are configured, the wrong WhatsApp number may send them.

### Do not run too many services on a small machine

Chrome uses a lot of memory. On an i7 Windows computer with 8 GB RAM, plan for:

- **recommended:** 2 to 3 sender services;
- **possible after monitoring:** 4 sender services;
- **avoid:** 5 or more sender services unless RAM is upgraded.

On a lighter Linux server, 3 to 4 services may be reasonable, but always monitor
RAM. If the server becomes slow or starts using swap/page file heavily, reduce
the number of services or upgrade RAM.

## Windows setup

For Windows, follow the dedicated guide: [`windows-service-setup.md`](windows-service-setup.md).
It includes native Windows Service commands and a Task Scheduler option that is
often easier for visible Chrome and QR-code login.

## Simple Linux service commands

Replace `ajk` with the ERP name you are checking.

Check status:

```bash
systemctl --user status whatsapp-sender-ajk
```

Start:

```bash
systemctl --user start whatsapp-sender-ajk
```

Stop:

```bash
systemctl --user stop whatsapp-sender-ajk
```

Restart:

```bash
systemctl --user restart whatsapp-sender-ajk
```

View logs:

```bash
journalctl --user -u whatsapp-sender-ajk -f
```

If your installation uses system-level services instead of user services, remove
`--user` from the commands.

## Setup worksheet

Fill this in for each ERP instance.

```text
ERP name:
Service name:
WhatsApp phone number:
Chrome profile folder:
Configuration file:
Topics or stream names:
Who scanned QR code:
Date linked:
Test message sent successfully: Yes / No
Notes:
```
