# Friend Groups and `.dnlgroup` Files

> **Prefer a visual walkthrough?** See [Friend Groups & `.dnlgroup` Setup Guide (PDF)](Dolphin-NetPlay-Launcher-Friend-Groups-Guide-v1.pdf).

Friend Groups are Dolphin NetPlay Launcher's repeat-play workflow for people who regularly use NetPlay together. The goal is to replace “send me the room code again” with a persistent group that can recognize who is hosting and prepare the existing Dolphin Join path for you.

## What a Friend Group is

A saved group contains a group name, Dolphin public-lobby region, shared NetPlay password, your own public session name for that group, the other members' session names, whether the group is preferred for hosting, and local-only LAN mappings. Dolphin NetPlay Launcher can keep multiple groups and one is active at a time.

The active group appears in matching dropdowns in **Host** and above **Join → Friends**. Switching that dropdown changes the member names, shared password, region, identity, and local LAN mappings used by the Friend workflow. The active group is also the group used when Friend hosting is enabled.

When **Join → Friends** is active, Dolphin NetPlay Launcher automatically copies the active group's local identity into the main **Nickname** field. Host does the same whenever **Use friend group** is enabled. Switching groups therefore updates Nickname to the identity chosen for that group. The Nickname field is still editable afterward; the synchronization is a convenience rather than a locked requirement.

Offline placeholder rows are intentionally not shown. The roster is for currently discoverable Friend lobbies, while the group dropdown is for switching which set of people Dolphin NetPlay Launcher is looking for. Your own identity is also intentionally excluded from the join roster.

## Create a group locally

1. Open **Options → Friends**.
2. Turn on **Enable Friend Groups**.
3. Use **New** if you want another group, then give it a recognizable name.
4. Enter the shared NetPlay password.
5. Set **My session name** to the name your Dolphin public lobby will use for this group.
6. Add the other members' session names, one per line.
7. Choose the Dolphin public-lobby region.
8. Press **OK**.

You can return to Options and use the Active group dropdown to edit any saved group. **Delete** removes only the local saved entry. It does not delete any `.dnlgroup` file previously exported.

## Switch between groups

Use the Friend Group dropdown in either **Host** or **Join → Friends**. Selecting another entry makes it the active group and refreshes Friend discovery for that group.

A typical setup might include separate entries such as:

- Family
- Main Discord
- Speedrun group
- Local friends

Only one group is active at once. The rest stay saved locally until selected.

## Share a group with `.dnlgroup`

The easiest group setup is to configure it once and share it.

1. In **Options → Friends**, select the group you want to share.
2. Choose **Export active group...**.
3. Save the `.dnlgroup` file.
4. Send it to the intended people — for example in a private group Discord.
5. On another PC, either choose **Import .dnlgroup...** or drag the file directly onto Dolphin NetPlay Launcher. If the launcher is running elevated as Administrator, Windows may block drag/drop from a normal Explorer window; use the explicit **Import .dnlgroup...** action instead.
6. The launcher asks **Which member are you?** Pick that machine's user.
7. The imported group is added to the local group list and becomes active. Everyone else becomes a Friend target; your own identity does not appear in the Join roster.

If a `.dnlgroup` is imported again with the same group name, the local entry is updated rather than creating another copy. Local LAN mappings are preserved because they belong to that PC, not to the shared file.

## What is inside a `.dnlgroup` file?

A `.dnlgroup` profile contains:

- Group name
- Public-lobby region
- Shared NetPlay password
- Complete member/session-name list

It does **not** contain:

- Your per-PC LAN addresses
- Direct-IP history
- Traversal room-code history
- General launcher settings

Optional custom member badges **are** included when present. They are stored as small normalized PNG images inside the group profile.

### Security

The shared password has to travel with the file or another computer could not import a ready-to-use group. The file therefore provides portability, **not secret storage**. Its contents should be treated as a shared group secret. Custom badges may also contain recognizable profile images. Do not post a `.dnlgroup` file publicly unless every included secret/image is appropriate for public redistribution.

Locally saved Friend Group data in `config.ini` is protected with Windows DPAPI. Normal portable settings exports intentionally omit Friend Group identities/secrets.

## Joining a Friend

When Friend Groups are configured, **Friends is the default Join connection**. Traversal and Direct IP remain available as explicit lower-level routes, but remembered room-code/IP history does not take priority over the Friend workflow when you enter Join.

1. Choose **Join**.
2. **Friends** should already be selected.
3. Pick the group from the dropdown if it is not already active.
4. The launcher checks Dolphin's public NetPlay lobby for exact configured member names whose group password can resolve a valid target.
5. Highlight a hosting Friend. Highlighting alone does not commit to that Friend.
6. Select the Friend to **load** them.
7. Use the normal **Join** action.

This keeps the established Join flow intact while avoiding manual room-code exchange.

## Hosting for a Friend Group

The active group can be selected directly from the **Host** Friend Group dropdown. Dolphin NetPlay Launcher fills that group's configured session name, password, and region into the existing Dolphin public-hosting workflow. A game is still required when hosting.

Friend Groups do not create a separate Dolphin NetPlay Launcher server or account system. Discovery continues to use Dolphin's public lobby.

## Same-network / LAN players

Some households need a local player to connect to the host's LAN address instead of using the route a remote Friend uses. This is handled per PC and per saved group.

On the joining computer:

1. Open **Options → Friends**.
2. Select the relevant group.
3. Under **Same-network / LAN connection**, choose the Friend who hosts on the same network.
4. Enter that host's local address/IP and Dolphin NetPlay port.
5. Save it.

The Friend is still discovered normally through the group's public-lobby matching. When that Friend is loaded, Dolphin NetPlay Launcher defaults to the saved local Direct-IP endpoint and shows a **Route: LAN** button in the Friends area. If that same person is temporarily hosting from another network, toggle it to **Route: Internet** for that join. The saved LAN address remains intact and becomes the default again the next time that Friend is loaded.

Remote group members without a LAN mapping keep using the normal Friend route. LAN mappings intentionally stay local and are not exported in `.dnlgroup`.

Depending on the router/NAT setup, a same-network Friend may sometimes connect successfully through the normal advertised/Traversal route without a LAN override. Port-forwarding and other network configuration can affect that behavior. The explicit per-Friend LAN Direct-IP mapping remains the deterministic household fallback when the normal route does not work.

## Personality badges

Friend roster badges are optional. Generated initials are the fallback. In the badge editor you can choose **your own identity or any other member**, then choose an image file or copy a profile picture (for example from Discord) and use **Paste image**. Dolphin NetPlay Launcher crops it to a small circular 64×64 PNG. Custom badges for every configured member—including the person exporting the file—are included in `.dnlgroup` exports and restored when another member imports the file. If you do not want to redistribute a profile picture, clear that custom badge before exporting.

## Troubleshooting

### A known Friend is not appearing

- Confirm the correct group is selected in the main dropdown.
- Confirm their saved member/session name exactly matches their public Dolphin session name.
- Confirm both sides are using the intended shared group password.
- Press **Refresh**.
- Remember that offline members are not shown as placeholder rows.

### I see myself in the Friend roster

The current design excludes the selected local identity. Re-import the group and make sure you choose the correct member when prompted.

### A household Friend is found but the connection route fails

Configure that Friend in the active group's **Same-network / LAN connection** section on the joining PC. Do not put IP addresses or `name=ip:port` syntax into the member-name list.

### I imported the file but it replaced/updated an existing group

An import with the same group name updates that local group entry. Use a different group name if you intend to keep two distinct groups.

### Dragging a `.dnlgroup` onto the launcher does nothing

If Dolphin NetPlay Launcher is running as Administrator while File Explorer is not, Windows may block the drag/drop operation across that privilege boundary. Use **Options → Friends → Import .dnlgroup...** instead. The file format/importer itself is not dependent on drag/drop.

## Scope

Friend Groups are a convenience layer around Dolphin NetPlay. Dolphin remains responsible for emulation, compatibility, NetPlay networking, and its public lobby. Dolphin NetPlay Launcher does not provide game files, Dolphin builds, cloud Friend accounts, or its own multiplayer network.

