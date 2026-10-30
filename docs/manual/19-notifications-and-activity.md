# 19. Notifications, watching, comments and activity

## The bell and the notification center

The **bell** at the top shows a badge with the number of unread notifications. Select it to open a flyout of the latest eight; select one to open the object (and, for a comment, the comment). New notifications appear within about a second without reloading.

![The bell flyout](images/awareness-bell.png)

**Notifications** (`/notifications`) has tabs **All, Unread, Mentions, Tasks, Watching**, grouped by day. Tick rows and mark them read in bulk.

![The notifications page](images/awareness-notifications.png)

If something you were told about is no longer readable to you, the row says only "A notification you can no longer view".

### Preferences

**Notifications > Preferences** chooses which kinds you receive. Mentions, tasks and approvals cannot be turned off, and the page says why. Optional e-mail delivery over an on-premises relay exists if your administrator turned it on ([ops/notifications.md](../ops/notifications.md)).

![Notification preferences](images/awareness-preferences.png)

### Confirming your e-mail address

E-mail is sent only to an address you have confirmed.

1. Under **E-mail**, tick **Send me e-mail when I have a notification**, type your address and choose **Save e-mail settings**. Keel PLM sends a confirmation link to that address and the page says **We sent a link**.
2. Open the link in the e-mail. You sign in, then press **Confirm my e-mail address**. Opening the link alone confirms nothing.
3. The preferences page now says **Your address is confirmed**, and the short "you have a notification" e-mail (it never names or describes anything) is sent from then on.

![E-mail settings waiting for the confirmation link](images/email-verification-preferences.png)
![The confirmation page](images/email-verification-confirm.png)

A link works once and for 24 hours; a newer link replaces it, and changing the address un-confirms it. If a link is not valid the page says so in one sentence and does not say why. If you ask for links too often the page asks you to wait. If mail to your confirmed address bounces or the recipient complains, Keel PLM stops sending to it and the preferences page tells you; ask an administrator to lift it (**Administration > E-mail delivery**), after which you confirm again.

## Watching

Select **Watch** next to the star on a part, document, change, baseline or MBOM (and many other objects). Choose a level, for example **Decisions only**. **Notifications > Things I watch** (`/watching`) lists them, and Home has the card **Activity in things I watch**. Creating or changing something auto-watches it unless you turn that off.

![Things I watch](images/awareness-watching.png)

## Comments and mentions

1. Open an object's **Comments** tab.
2. Type your comment. Type `@` and a few letters to pick a person: only people who can read the object are offered, so lena is not offered on an ITAR part.
3. Select **Post comment**. Reply to a comment from its thread; removing one leaves a tombstone. Comments are immutable and rate limited.
4. Viewers, authors, approvers and administrators can comment; auditors only read.

![The comments tab](images/parts-comments-tab.png)

## Activity

The **Activity** tab tells in sentences who did what and when, with **Show details**. Views and downloads by colleagues are not listed (an auditor can switch them on).

![The activity tab](images/parts-activity-tab.png)
