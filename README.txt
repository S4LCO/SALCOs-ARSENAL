IF YOU CHANGE FROM SALCOS ARSENAL WITH TRADER TO THIS VERSION - READ CAREFULLY!!!
IF ENGLISCH IS NOT YOUR NATIVE LANGUAGE, USE A TRANSLATOR!


STEP 1:
Go to .../SPT/user/ and make a backup of the "profiles" folder.


STEP 2:
Go to .../SPT/user/mods and delete the "SalcosArsenal_v1.0.0" folder.


STEP 3:
Now go to .../SPT/user/cache and delete all the files in it.


STEP 4:
Go to the main folder of SPT. Open the "BepInEx" folder and then "cache".
Delete all files in this folder as well.


STEP 5.
Place the new SalcosArsenal_v1.0.0 without trader in the designated folder.


STEP 6:
Now start the server. You will see an error message in the server browser.
If this happens, go to: .../SPT/SPT_Data/configs and open the file "core.json".


STEP 7:
In "core.json" you will see the following lines:

"removeModItemsFromProfile": false,
"removeInvalidTradersFromProfile": false,
"fixProfileBreakingInventoryItemIssues": false,

Wherever you see "false", change it to "true", save the file and restart the server AND the launcher until you are in your stash.


STEP 8:
Then close the game completely and undo step 7 (change everything you changed to "true" back to "false").
Then save the file again and you can start the game normally.


SPT should now have removed the trader from your profile.
