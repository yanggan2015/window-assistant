// 消除 WinForms 隐式全局 using 与 WPF 的类型歧义（本项目 UI 使用 WPF）。
global using System.Windows;
global using MessageBox = System.Windows.MessageBox;
global using Button = System.Windows.Controls.Button;
global using Clipboard = System.Windows.Clipboard;
