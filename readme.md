# CSI HyperV Driver

This repository hosts the CSI HyperV driver and all of its build and dependent configuration files to deploy the driver.


## Pre-requisite

- A HyperV Failover cluster or share nothing HyperV array
- Kubernete cluster with nodes running nested on HyperV
- All Kubernetes nodes should have `hyperv-daemons` installed
- Powershell 6+ should to be installed on all HyperV hosts
- SSHd (OpenSSH server) need to be running on all HyperV hosts
- It should be possible to connect from the csi-driver container 
  to all HyperV hosts with Powershell over SSH transport
- one SSH PupPrivkey

## install requisites

### HyperV Host: OpernSSH Server and Powershell Core
on each HyperV Host run in powershell:
```
#install sshd
Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0
Start-Service sshd
Set-Service -Name sshd -StartupType 'Automatic'

#to check the filewall rule
Get-NetFirewallRule -Name *ssh*

#install pwsh
iex "& { $(irm https://aka.ms/install-powershell.ps1) } -UseMSI"

#workaround for some sshd bug
New-Item -ItemType SymbolicLink -path C:\ -Name pwsh -Target "C:\Program Files\PowerShell\6"
```

Edit the sshd_config file located at $env:ProgramData\ssh.
```
PubkeyAuthentication yes
#PasswordAuthentication yes
Subsystem	powershell	c:/pwsh/pwsh.exe -sshs -NoLogo -NoProfile
```
more info under https://stackoverflow.com/questions/16212816/setting-up-openssh-for-windows-using-public-key-authentication

Edit or create the file $env:ProgramData\ssh\administrators_authorized_keys
and add your personal ssh pubkey to the file.
Ensure that only System and Administrators in the UAC permissions, 
or else sshd will not take it.

Restart ssdh with `Restart-Service sshd`

Test connection from your windows client:
```
Enter-PSSession <ServerFQDN> --SSHTransport -UserName Administrator
```

### Kubernetes Node
centos/rhel:
```
yum install -y hyperv-daemons
```

Optional add to all kubernetes VM's up to 4 scsi controllers 
one scsi controller supports 64 disks 

### SSH

create a new ssh key
```
ssh-keygen -a 100 -t ed25519 -f ~/path/to/local-ssh-keys -C "csi-hyperv"

ssh-keyscan -H -t ed25519 server1 server2 > ~/path/to/known_hosts
ssh-keyscan -H -t ed25519 server1.domain.local server2.domain.local >> ~/path/to/known_hosts
```



## Deploy Kubernetes

create a ssh secret 
```
kubectl create secret generic csi-hyperv-key --from-file=id_ed25519=~/path/to/local-ssh-keys --from-file=known_hosts=~/path/to/known_hosts
```

## Run example application and validate


## Confirm Hostpath driver works