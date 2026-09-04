@echo off
echo =====================================================
echo  NagiBridge - clear stuck host port 58331
echo =====================================================
echo.
echo  STEP 1: CLOSE both Stardew windows (host + farmhand)
echo          completely before running this.
echo  STEP 2: This MUST run as Administrator.
echo.
pause

echo.
echo  Deleting poisoned URL reservations on port 58331 ...
netsh http delete urlacl url=http://+:58331/
netsh http delete urlacl url=http://localhost:58331/
echo.

echo  Restarting HTTP.sys to clear any stale queue ...
net stop http
net start http
echo.

echo -----------------------------------------------------
echo  CHECK: there should be NO "58331" line below.
echo -----------------------------------------------------
netsh http show urlacl | findstr 58331
echo -----------------------------------------------------
echo.
echo  If nothing with 58331 appears above, it's clean.
echo  Now reopen BOTH Stardew instances (host first).
echo  Tell me and I'll verify port 58331.
echo.
pause
