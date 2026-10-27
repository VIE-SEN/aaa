@echo off
chcp 65001 >nul
cd /d %~dp0
echo ========================================
echo   FongMi Live Config Generator - Server
echo.
echo   Open in browser:
echo   http://127.0.0.1:8899
echo.
echo   Press Ctrl+C to stop
echo ========================================
node server.js
pause
