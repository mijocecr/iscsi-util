using System;
using System.Runtime.CompilerServices;
using System.Xml;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ISCSI_Util.ViewModels
{
    /// <summary>
    /// View model for the password input dialog.
    /// Collects admin password from user and passes it back via callback.
    /// </summary>
    public class PasswordDialogViewModel : ObservableObject
    {
        /// <summary>The password entered by the user.</summary>
        private string _password;
        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value);
        }

        /// <summary>Command to accept and submit the entered password.</summary>
        public RelayCommand AceptarCommand { get; }

        /// <summary>
        /// Initializes the password dialog view model.
        /// Sets up the accept command to pass the password back to the callback.
        /// </summary>
        public PasswordDialogViewModel(Action<string> onPasswordEntered)
        {
            AceptarCommand = new RelayCommand(() =>
            {
                // Invoke callback with the entered password
                onPasswordEntered?.Invoke(Password);
            });
        }

    }
}