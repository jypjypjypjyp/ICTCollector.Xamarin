﻿using Android.App;
using Android.Opengl;
using Android.OS;
using Android.Support.Design.Widget;
using Android.Util;
using Android.Views;
using Android.Widget;
using MySLAM.Xamarin.Helpers;
using MySLAM.Xamarin.Helpers.AR;
using MySLAM.Xamarin.Helpers.Calibrator;
using MySLAM.Xamarin.Views;
using Org.Opencv.Android;
using Org.Opencv.Core;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MySLAM.Xamarin
{

    public class MyARFragment : Fragment,
                                ILoaderCallbackInterface,
                                CameraBridgeViewBase.ICvCameraViewListener2
    {
        public JavaCameraView CameraView;
        public GLSurfaceView GLSurfaceView;

        public MyARHelper ARHelper { get; set; }

        private MyARRenderer renderer;
        private TextView textView;

        public override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
        }
        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            return inflater.Inflate(Resource.Layout.ar_frag, container, false);
        }
        public override void OnViewCreated(View view, Bundle savedInstanceState)
        {
            textView = view.FindViewById<TextView>(Resource.Id.pose_textview);
            view.FindViewById<FloatingActionButton>(Resource.Id.fab).Click +=
                (o, e) =>
                {
                    Activity.OpenOptionsMenu();
                };
            CameraView = view.FindViewById<JavaCameraView>(Resource.Id.ar_view);
            CameraView.SetMaxFrameSize(800, 550);
            CameraView.SetCvCameraViewListener2(this);
            CameraView.Click += OnClick;

            //OpenGL 
            GLSurfaceView = view.FindViewById<GLSurfaceView>(Resource.Id.gl_view);
            GLSurfaceView.SetEGLContextClientVersion(3);
            GLSurfaceView.SetEGLConfigChooser(8, 8, 8, 8, 16, 0); //Set Transparent
            GLSurfaceView.Holder.SetFormat(Android.Graphics.Format.Translucent);
            renderer = new MyARRenderer();
            GLSurfaceView.SetRenderer(renderer);
            GLSurfaceView.RenderMode = Rendermode.Continuously;
            GLSurfaceView.SetZOrderOnTop(true);
        }

        public override void OnResume()
        {
            base.OnResume();
            if (!OpenCVLoader.InitDebug())
            {
                OpenCVLoader.InitAsync(OpenCVLoader.OpencvVersion300, Activity, this);
            }
            else
            {
                OnManagerConnected(LoaderCallbackInterface.Success);
            }
        }
        public override void OnPause()
        {
            base.OnPause();
            if (CameraView != null)
            {
                CameraView.DisableView();
                HelperManager.CameraHelper.CameraLock.Release();
            }
        }
        public override void OnDestroy()
        {
            (ARHelper?.FrameRender as ARFrameRender)?.Dispose();
            base.OnDestroy();
        }

        #region OptionMenu
        public override void OnCreateOptionsMenu(IMenu menu, MenuInflater inflater)
        {
            inflater.Inflate(Resource.Menu.ar, menu);
        }
        public override void OnPrepareOptionsMenu(IMenu menu)
        {
            if (ARHelper == null)
                return;
            menu.SetGroupVisible(Resource.Id.mode_ar, false);
            if (ARHelper.CameraCalibrator.IsCalibrated)
            {
                menu.FindItem(Resource.Id.action_change_mode).SetVisible(true);
                if (ARHelper.FrameRender is ARFrameRender)
                {
                    menu.SetGroupVisible(Resource.Id.mode_ar, true);
                }
            }
            else
            {
                menu.FindItem(Resource.Id.action_change_mode).SetVisible(false);
            }
        }
        public override bool OnOptionsItemSelected(IMenuItem item)
        {
            switch (item.ItemId)
            {
                case Resource.Id.action_calibrate:
                    Calibrate();
                    break;
                case Resource.Id.render_ar:
                    StartAR();
                    item.SetChecked(true);
                    break;
                case Resource.Id.render_none:
                    ARHelper.ChangeRenderMode<PreviewFrameRender>();
                    item.SetChecked(true);
                    break;
                case Resource.Id.model_square:
                    renderer.ManageEntitys((e) =>
                    {
                        e["still square 1"] = new StillSquare(1.5f);
                    });
                    break;
            }
            return true;
        }
        #endregion

        private async void StartAR()
        {
            var dialogFragment = new MyDialog(DialogType.ProgressHorizontal, Resources.GetString(Resource.String.loading_voc));
            dialogFragment.Show(FragmentManager, "Progress Dialog");
            RegisterProgressChangedCallback(
                a =>
            Activity.RunOnUiThread(
                () => ((ProgressDialog)dialogFragment.Dialog).Progress = a));
            await Task.Run(() =>
            {
                ARHelper.ChangeRenderMode<ARFrameRender>();
                ((ARFrameRender)ARHelper.FrameRender).Perpare(renderer.VMat);
                //NeedToBeRemoved: temp code
                ARFrameRender.Update += Update;
                UnRegisterProgressChangedCallback();
            });
            dialogFragment.Dismiss();
        }

        //NeedToBeRemoved: temp code
        private void Update(float[] fs)
        {
            string s = "";
            int i = 0;
            foreach(var f in fs)
            {
                s += f.ToString("F")+" ";
                i++;
                if (i % 4 == 0) s += "\n";
            }
            Activity.RunOnUiThread(() =>
            {
                textView.Text = s;
            });
        }

        private void Calibrate()
        {
            if (ARHelper.CameraCalibrator.IsCalibrated)
            {
                new MyDialog(DialogType.Error, Resources.GetString(Resource.String.not_calibrate))
                {
                    PositiveHandler = (o, e) =>
                    {
                        ((Dialog)o).Dismiss();
                    }
                }.Show(FragmentManager, null);
                ARHelper.ChangeRenderMode<CalibrationFrameRender>();
                ARHelper.CameraCalibrator.IsCalibrated = false;
                return;
            }
            if (ARHelper.CameraCalibrator.CornersBufferSize < 2)
            {
                Toast.MakeText(Activity, Resource.String.more_samples, ToastLength.Short).Show();
                return;
            }
            //Perpare Progress Dialog
            ARHelper.ChangeRenderMode<PreviewFrameRender>();
            var dialogFragment = new MyDialog(DialogType.Progress, Resources.GetString(Resource.String.please_wait));
            dialogFragment.Show(FragmentManager, "Progress Dialog");

            Task.Run(() => ARHelper.CameraCalibrator.Calibrate())
                .ContinueWith(t =>
                {
                    dialogFragment.Dismiss();
                    ARHelper.CameraCalibrator.ClearCorners();
                    string resultMessage = "";
                    if (ARHelper.CameraCalibrator.IsCalibrated)
                    {
                        resultMessage = Resources.GetString(Resource.String.calibration_successful);
                        ARHelper.Save(ARHelper.CameraCalibrator.CameraMatrix);
                        ARHelper.CameraCalibrator.IsCalibrated = true;
                    }
                    else
                    {
                        resultMessage = Resources.GetString(Resource.String.calibration_unsuccessful);
                    }
                    Toast.MakeText(Activity, resultMessage, ToastLength.Long).Show();
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void UpdatePose(float[] pose)
        {
            string text = "";
            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 4; j++)
                {
                    text += pose[4 * i + j].ToString("F") + " ";
                }
                text += '\n';
            }
            Activity.RunOnUiThread(() => textView.Text = text);

        }

        private void OnClick(object sender, System.EventArgs e)
        {
            if (ARHelper.FrameRender is CalibrationFrameRender)
            {
                if (ARHelper.CameraCalibrator.AddCorners())
                {
                    Toast.MakeText(Activity, Resource.String.add_corners, ToastLength.Short).Show();
                }
            }
        }

        #region ICvCameraViewListener2
        public Mat OnCameraFrame(CameraBridgeViewBase.ICvCameraViewFrame p0)
        {
            var mat = ARHelper?.FrameRender.Render(p0);
            return mat;
        }
        public async void OnCameraViewStarted(int width, int height)
        {
            if (ARHelper == null)
            {
                //Creating a calibratorHelper may take long time. So I make it async, and add a progress dialog.
                var dialogFragment = new MyDialog(DialogType.ProgressHorizontal, Resources.GetString(Resource.String.please_wait));
                dialogFragment.Show(FragmentManager, "Progress Dialog");
                dialogFragment.NegativeHandler = (o, e) => MyARHelper.RemoveCache();
                ARHelper = await MyARHelper.AsyncBuilder(Activity, width, height,
                (i) =>
                {
                    Activity.RunOnUiThread(() =>
                        {
                            ((ProgressDialog)dialogFragment.Dialog).Progress = i;
                        });
                });
                dialogFragment.Dismiss();

                if (!ARHelper.CameraCalibrator.IsCalibrated)
                {
                    new MyDialog(DialogType.Error, Resources.GetString(Resource.String.not_calibrate))
                    {
                        PositiveHandler = (o, e) =>
                        {
                            ((Dialog)o).Dismiss();
                        }
                    }.Show(FragmentManager, null);
                }
            }

            SetHasOptionsMenu(true);
        }
        public void OnCameraViewStopped()
        {
        }
        #endregion

        #region ILoaderCallbackInterface
        public void OnManagerConnected(int p0)
        {
            switch (p0)
            {
                case LoaderCallbackInterface.Success:
                    Log.Info("OpenCV4Android", "OpenCV loaded successfully");
                    HelperManager.CameraHelper.CameraLock.WaitOne();
                    CameraView.EnableView();
                    break;
                default:
                    break;
            }
        }
        public void OnPackageInstall(int p0, IInstallCallbackInterface p1)
        {
        }
        #endregion

        #region Native
        [DllImport("MySLAM_Native", EntryPoint = "RegisterProgressChangedCallback")]
        private static extern void RegisterProgressChangedCallback(MyDialog.ProgressChanged callback);
        [DllImport("MySLAM_Native", EntryPoint = "UnRegisterProgressChangedCallback")]
        private static extern void UnRegisterProgressChangedCallback();
        #endregion
    }
}