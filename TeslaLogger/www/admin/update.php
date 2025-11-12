<?PHP
	require_once("tools.php");

	unlink("/etc/teslalogger/cmd_updated.txt");
	$dockerfile = "/tmp/teslalogger-DOCKER";

	$time = date("d.m.Y H:i:s", time());
	$ret2 =	file_put_contents($logfile, $time . " : UPDATE request!\r\n", FILE_APPEND);
	$ret2 =	file_put_contents($logfile, $time . " : --------------------------------------------\r\n\r\n", FILE_APPEND);

	sleep(2);

	if (file_exists($dockerfile))
	{
		$ret2 =	file_put_contents($logfile, $time . " : Docker detected\r\n", FILE_APPEND);
		file_put_contents("/tmp/teslalogger-cmd-restart.txt","update");

		echo ("OK");
	}
	else
	{
		$output  = shell_exec('sudo /sbin/reboot');
		if ($output === false) {
			// Handle the error
			echo "error";
		}
		else
			echo ("OK");
	}
?>
