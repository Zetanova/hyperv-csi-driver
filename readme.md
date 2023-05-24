# CSI HyperV Driver

This repository hosts the CSI HyperV driver and all of its build and dependent configuration files to deploy the driver.

## Information

The driver connects to the HyperV Host, creates a VirtualDisk 
and attaches it to the requested VirtualMachine.

## Pre-requisite

- A HyperV Failover cluster or share nothing HyperV array
- Kubernete cluster with nodes running nested on HyperV
- All Kubernetes nodes should have `hyperv-daemons` installed
- Powershell 6+ should to be installed on all HyperV hosts
- SSHd (OpenSSH server) need to be running on all HyperV hosts
- It should be possible to connect from the csi-driver container 
  to all HyperV hosts with Powershell over SSH transport
- one SSH PupPrivkey

## Install requisites

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
New-Item -ItemType SymbolicLink -path C:\ -Name pwsh -Target "C:\Program Files\PowerShell\7"
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

### HyperV Host: Storage

Currently only ClusterStorageVolume is supported.

To enable storage of VirtualDisks on a CSV,
create one subfolder "Volumes" on the CSV

The volume property "Storage" is to specify the CSV by name.
`C:\ClusterStorage\<Storage>\Volumes\<VolumeId>.vdhx`

### Kubernetes Node
centos/rhel:
```
#install hyperv kvp daemon
sudo yum install -y hyperv-daemons

#install pwsh
curl https://packages.microsoft.com/config/rhel/7/prod.repo | sudo tee /etc/yum.repos.d/microsoft.repo
sudo yum install -y powershell

#edit /etc/ssh/sshd_config
#add: Subsystem powershell /usr/bin/pwsh -sshs -NoLogo -NoProfile

sudo shutdown -r now
```

ubuntu
```
sudo apt-get update
sudo apt-get install -y linux-cloud-tools-generic

sudo apt-get install -y powershell

#edit /etc/ssh/sshd_config
#add: Subsystem powershell /usr/bin/pwsh -sshs -NoLogo -NoProfile

sudo shutdown -r now
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

create a ssh secret and deploy the driver
```
kubectl create secret generic csi-hyperv-key --from-file=id_ed25519=~/path/to/local-ssh-keys --from-file=known_hosts=~/path/to/known_hosts

#static namespace csi-hyperv
kubectl apply -f .\deploy\kubernetes-1.19\csi-hyperv\rbac.yaml
kubectl apply -f .\deploy\kubernetes-1.19\csi-hyperv\controller.yaml
kubectl apply -f .\deploy\kubernetes-1.19\csi-hyperv\node.yaml
```

## Run example application and validate

To test connection from the container to the HyperV host servers
```
kubectl apply -f .\examples\pwsh-test.yaml

#after few moments
kubectl exec -it pwsh-test -- pwsh

#test with:
Enter-PSSession server1 -UserName administrator
Enter-PSSession server1.domain.local -UserName administrator
```

## Confirm HyperV driver works

```
kubectl apply -f .\examples\csi-storageclass.yaml
kubectl apply -f .\examples\csi-pvc.yaml

#after few moments 
kubectl get pvc csi-pvc

#verify that the VirtualDisk on the storage was created
#ClusterStorage\<Storage>\Volumes\<VolumeId>.vhdx

#use the PVC as volume mount
```

## Limitiations & Improvements

- Currently only ClusterStorageVolumes are supported.
The cluster storage can run over S2D or SOFS.
- The support of a shared-nothing HyperV Host Array is technically possible, 
but not implemented.

## Build

### docker build
cd src
docker build -f hyperv-csi-driver\Dockerfile -t zetanova/hyperv-csi-driver:latest .
docker push zetanova/hyperv-csi-driver:latest

## Deployment

running since Jan. 2020
tested up to kubernetes 1.20 
