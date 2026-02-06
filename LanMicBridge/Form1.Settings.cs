namespace LanMicBridge;

partial class Form1
{
    private void LoadAppSettings()
    {
        _appSettings = AppSettings.Load();
        ApplySettingsToUi(_appSettings);
    }

    private void SaveAppSettings()
    {
        var settings = CollectAppSettings();
        settings.Save();
        _appSettings = settings;
    }

    private void ApplySettingsToUi(AppSettings settings)
    {
        _loadingSettings = true;
        try
        {
            var outputIndex = FindRenderDeviceIndex(settings);
            if (outputIndex >= 0 && outputIndex < _comboOutputDevice.Items.Count)
            {
                _comboOutputDevice.SelectedIndex = outputIndex;
            }

            if (settings.JitterIndex is int jitterIndex &&
                jitterIndex >= 0 &&
                jitterIndex < _comboJitter.Items.Count)
            {
                _comboJitter.SelectedIndex = jitterIndex;
            }

            if (settings.OutputGainPercent is int outputGainPercent)
            {
                _trackOutputGain.Value = Math.Clamp(outputGainPercent, _trackOutputGain.Minimum, _trackOutputGain.Maximum);
                UpdateOutputGain();
            }

            if (settings.OutputForceStartMs is int outputForceStartMs)
            {
                var value = Math.Clamp(outputForceStartMs / 100, _trackOutputForceStart.Minimum, _trackOutputForceStart.Maximum);
                _trackOutputForceStart.Value = value;
                UpdateOutputForceStart();
            }

            if (settings.RecvProcessingEnabled.HasValue)
            {
                _chkRecvProcessing.Checked = settings.RecvProcessingEnabled.Value;
            }

            if (!string.IsNullOrWhiteSpace(settings.SenderIp))
            {
                _txtIp.Text = settings.SenderIp;
            }

            if (settings.CaptureApiIndex is int apiIndex &&
                apiIndex >= 0 &&
                apiIndex < _comboCaptureApi.Items.Count)
            {
                _comboCaptureApi.SelectedIndex = apiIndex;
            }

            var micIndex = FindCaptureDeviceIndex(settings);
            if (micIndex >= 0 && micIndex < _comboMicDevice.Items.Count)
            {
                _comboMicDevice.SelectedIndex = micIndex;
            }

            if (settings.QualityIndex is int qualityIndex &&
                qualityIndex >= 0 &&
                qualityIndex < _comboQuality.Items.Count)
            {
                _comboQuality.SelectedIndex = qualityIndex;
            }
            ApplyQualitySelection();

            if (settings.SendModeIndex is int sendModeIndex &&
                sendModeIndex >= 0 &&
                sendModeIndex < _comboSendMode.Items.Count)
            {
                _comboSendMode.SelectedIndex = sendModeIndex;
            }

            if (settings.SendGainPercent is int sendGainPercent)
            {
                _trackGain.Value = Math.Clamp(sendGainPercent, _trackGain.Minimum, _trackGain.Maximum);
                UpdateGain();
            }

            if (settings.SendProcessingEnabled.HasValue)
            {
                _chkSendProcessing.Checked = settings.SendProcessingEnabled.Value;
            }

            if (settings.SendTestToneEnabled.HasValue)
            {
                _chkSendTestTone.Checked = settings.SendTestToneEnabled.Value;
            }

            if (settings.VadThresholdDb.HasValue)
            {
                var value = (int)Math.Round(-settings.VadThresholdDb.Value);
                _trackVadThreshold.Value = Math.Clamp(value, _trackVadThreshold.Minimum, _trackVadThreshold.Maximum);
                UpdateVadThreshold();
            }

            var receiverMode = !string.Equals(settings.LastMode, "Sender", StringComparison.OrdinalIgnoreCase);
            _radioReceiver.Checked = receiverMode;
            _radioSender.Checked = !receiverMode;
        }
        finally
        {
            _loadingSettings = false;
        }

        // 接続情報の折りたたみ状態を復元（未設定=初回は表示状態）
        var connInfoVisible = settings.ConnectionInfoVisible ?? true;
        _panelConnectionInfo.Visible = connInfoVisible;
        _linkConnectionInfo.Text = connInfoVisible ? "▼ 接続情報を隠す" : "▶ 接続情報を表示";

        var settingsVisible = settings.SettingsVisible ?? false;
        var tabIndex = settings.SettingsTabIndex ?? -1;
        if (settingsVisible)
        {
            if (tabIndex < 0)
            {
                if (settings.ReceiverDetailVisible == true)
                {
                    tabIndex = 0;
                }
                else if (settings.SenderDetailVisible == true)
                {
                    tabIndex = 1;
                }
                else
                {
                    tabIndex = 0;
                }
            }

            OpenSettingsTab(tabIndex);
        }
    }

    private AppSettings CollectAppSettings()
    {
        var settings = new AppSettings
        {
            LastMode = _radioSender.Checked ? "Sender" : "Receiver",
            JitterIndex = _comboJitter.SelectedIndex,
            OutputGainPercent = _trackOutputGain.Value,
            OutputForceStartMs = _outputForceStartMs,
            RecvProcessingEnabled = _chkRecvProcessing.Checked,
            ReceiverDetailVisible = _settingsForm != null && _settingsForm.Visible && _settingsForm.SelectedTabIndex == 0,
            SenderIp = _txtIp.Text.Trim(),
            CaptureApiIndex = _comboCaptureApi.SelectedIndex,
            QualityIndex = _comboQuality.SelectedIndex,
            SendModeIndex = _comboSendMode.SelectedIndex,
            SendGainPercent = _trackGain.Value,
            SendProcessingEnabled = _chkSendProcessing.Checked,
            SendTestToneEnabled = _chkSendTestTone.Checked,
            SenderDetailVisible = _settingsForm != null && _settingsForm.Visible && _settingsForm.SelectedTabIndex == 1,
            VadThresholdDb = _vadThresholdDb,
            SettingsVisible = _settingsForm != null && _settingsForm.Visible,
            SettingsTabIndex = _settingsForm != null ? _settingsForm.SelectedTabIndex : null,
            ConnectionInfoVisible = _panelConnectionInfo.Visible
        };

        if (_comboOutputDevice.SelectedIndex >= 0 &&
            _comboOutputDevice.SelectedIndex < _renderDevices.Count)
        {
            var device = _renderDevices[_comboOutputDevice.SelectedIndex];
            settings.OutputDeviceId = device.ID;
            settings.OutputDeviceName = device.FriendlyName;
            settings.OutputDeviceIndex = _comboOutputDevice.SelectedIndex;
        }

        if (_comboCaptureApi.SelectedIndex == 1)
        {
            settings.CaptureMmeIndex = _comboMicDevice.SelectedIndex;
            if (_comboMicDevice.SelectedIndex >= 0 && _comboMicDevice.SelectedIndex < _mmeDevices.Count)
            {
                settings.CaptureDeviceName = _mmeDevices[_comboMicDevice.SelectedIndex].ProductName;
            }
        }
        else
        {
            settings.CaptureDeviceIndex = _comboMicDevice.SelectedIndex;
            if (_comboMicDevice.SelectedIndex >= 0 && _comboMicDevice.SelectedIndex < _captureDevices.Count)
            {
                var device = _captureDevices[_comboMicDevice.SelectedIndex];
                settings.CaptureDeviceId = device.ID;
                settings.CaptureDeviceName = device.FriendlyName;
            }
        }

        return settings;
    }

    private int FindRenderDeviceIndex(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.OutputDeviceId))
        {
            for (var i = 0; i < _renderDevices.Count; i++)
            {
                if (string.Equals(_renderDevices[i].ID, settings.OutputDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.OutputDeviceName))
        {
            for (var i = 0; i < _renderDevices.Count; i++)
            {
                if (string.Equals(_renderDevices[i].FriendlyName, settings.OutputDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        if (settings.OutputDeviceIndex is int index && index >= 0 && index < _renderDevices.Count)
        {
            return index;
        }

        return -1;
    }

    private int FindCaptureDeviceIndex(AppSettings settings)
    {
        if (_comboCaptureApi.SelectedIndex == 1)
        {
            if (settings.CaptureMmeIndex is int mmeIndex &&
                mmeIndex >= 0 &&
                mmeIndex < _mmeDevices.Count)
            {
                return mmeIndex;
            }

            if (!string.IsNullOrWhiteSpace(settings.CaptureDeviceName))
            {
                for (var i = 0; i < _mmeDevices.Count; i++)
                {
                    if (string.Equals(_mmeDevices[i].ProductName, settings.CaptureDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        if (!string.IsNullOrWhiteSpace(settings.CaptureDeviceId))
        {
            for (var i = 0; i < _captureDevices.Count; i++)
            {
                if (string.Equals(_captureDevices[i].ID, settings.CaptureDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.CaptureDeviceName))
        {
            for (var i = 0; i < _captureDevices.Count; i++)
            {
                if (string.Equals(_captureDevices[i].FriendlyName, settings.CaptureDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        if (settings.CaptureDeviceIndex is int captureIndex &&
            captureIndex >= 0 &&
            captureIndex < _captureDevices.Count)
        {
            return captureIndex;
        }

        return -1;
    }

    private void OpenSettingsTab(int tabIndex)
    {
        EnsureSettingsForm();
        if (_settingsForm == null)
        {
            return;
        }

        _settingsForm.SelectedTabIndex = tabIndex;
        if (!_settingsForm.Visible)
        {
            _settingsForm.Show(this);
        }
        else
        {
            _settingsForm.BringToFront();
        }
    }

    private void EnsureSettingsForm()
    {
        if (_settingsForm != null)
        {
            return;
        }

        _groupReceiverDetail.Visible = true;
        _groupSenderDetail.Visible = true;
        _receiverInfoGroup.Visible = true;

        _settingsForm = new SettingsForm(_groupReceiverDetail, _groupSenderDetail, _receiverInfoGroup);
        _settingsForm.FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                _settingsForm.Hide();
                SaveAppSettings();
            }
        };
    }
}
