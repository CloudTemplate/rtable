for /f %%i in (%~dp0\parallel-tests.txt) do (
  del %%i.results.xml
  start "%%i" ..\..\packages\NUnit.Runners.2.6.4\tools\nunit-console.exe Microsoft.Azure.Toolkit.Replication.Test.dll /run=%%i /xml=%%i.results.xml
)

for /f %%i in (%~dp0\serial-tests.txt) do (
  del %%i.results.xml
  start "%%i" /wait ..\..\packages\NUnit.Runners.2.6.4\tools\nunit-console.exe Microsoft.Azure.Toolkit.Replication.Test.dll /run=%%i /xml=%%i.results.xml
)
