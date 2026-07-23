ROBO CASK & TAP POS 2.0.0 - NO PYTHON EDITION
================================================

THIS PACKAGE REPLACES THE PREVIOUS PYTHON-BASED PACKAGE.

It uses standard components already included with Windows 10/11:
- Windows PowerShell
- Microsoft Edge or the computer's default web browser

No Python, Node.js, database server, verification code or paid subscription is required.

INSTALLATION
------------
1. Extract the ZIP file completely. Do not run Setup from inside the ZIP preview.
2. Open the extracted folder.
3. Double-click: SETUP_ROBO_CASK_TAP_POS.cmd
4. A graphical installation wizard will appear.
5. Click Next, Next, Install and Finish.
6. Use the Desktop shortcut: ROBO CASK & TAP POS

IMMEDIATE TEST WITHOUT INSTALLATION
-----------------------------------
Double-click RUN_PORTABLE_NOW.cmd.
This opens the complete application directly for testing.

FIRST LOGIN
-----------
Administrator:
  Username: baron
  Password: Baron@123

Teller One:
  Username: teller1
  Password: Teller1@123

Teller Two:
  Username: teller2
  Password: Teller2@123

Each temporary password must be changed after first login.

DATA AND BACKUPS
----------------
Installed business data is stored in:
%LOCALAPPDATA%\ROBO CASK TAP POS\Data

Use Settings & Backup inside the software to download a JSON backup and create
an additional server backup copy. Keep regular copies on a flash drive or Google Drive.

RECEIPT PRINTER
---------------
Use an 80 mm thermal printer installed in Windows.
When printing:
- Paper width: 80 mm
- Scale: 100%
- Margins: none or minimum
- Headers and footers: off

The receipt intentionally has no QR code or verification code.

SHORT-GLASS STOCK
-----------------
1. Create a non-sellable stock item measured in ml, for example:
   Waragi Open Stock, quantity 7500 ml.
2. Create a sellable item such as Waragi Short 50 ml.
3. Select Waragi Open Stock as its Stock Source.
4. Set Deduct From Source Per Sale to 50.
Each short-glass sale will deduct 50 ml automatically.

SUPPORT / DIAGNOSTICS
---------------------
Run REPAIR_AND_DIAGNOSE.cmd if the software does not open.
A diagnostic report and server logs are stored in the Data folder.
