NEXUS POS - FIRST STEPS
=======================
Product: Nexus POS
Publisher: Nexus / www.ecaturonald.tech

CUSTOMER INSTALLATION
---------------------
Nexus POS is supplied as a self-contained 64-bit Windows application. A customer
computer does not need the .NET SDK or a separately installed .NET runtime.
Use the signed Nexus installer and the verified INSTALL_NEXUS_POS helper supplied
with the release.

FIRST LOGIN
-----------
There is no hard-coded public password.

On first start, the native launcher creates a strong temporary administrator
password in FIRST_LOGIN_CREDENTIALS.txt inside the Nexus POS data folder and opens
it in Notepad. The initial username is admin.

The administrator must change the password immediately. Delete the credential file
after the password is changed and securely recorded.

BUSINESS SETUP
--------------
After login, open Administration / Settings and enter the business trading name,
address, phone, email, three-letter currency code, receipt footer and business logo.
Then add categories, products, suppliers, opening stock, teller accounts and printer
settings. The customer's business identity appears in the application and documents;
Nexus remains the software publisher.

DATA AND BACKUPS
----------------
Installed data: %LOCALAPPDATA%\Nexus POS\Data
Audit documents: %PUBLIC%\Documents\Nexus POS\Audit Documents

Create and verify a backup before every upgrade and at least daily during business
use. Keep a second encrypted copy that is not permanently attached to the POS PC.

THERMAL RECEIPT PRINTER
-----------------------
Recommended browser print settings for an 80 mm printer:
- Paper width: 80 mm
- Scale: 100%
- Margins: none or minimum
- Browser headers and footers: off

NETWORK ACCESS
--------------
Local mode is the default and safest mode. Enable private shop-network or Cloudflare
access only after following the included security guide.

SUPPORT
-------
Run the installed Repair and Diagnose shortcut. Do not send passwords, temporary
credentials or live customer databases through public support channels.
