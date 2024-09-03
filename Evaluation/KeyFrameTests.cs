using Microsoft.VisualStudio.TestTools.UnitTesting;
using AGVSystem.Evaluation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EquipmentManagment.MainEquipment;

namespace AGVSystem.Evaluation.Tests
{
    [TestClass()]
    public class KeyFrameTests
    {
        [TestMethod()]
        public void KeyFrameTest()
        {
            var keyFrame = new KeyFrame();
            keyFrame.EQControls.Add(new EQControl()
            {
                EQ = new clsEQ(new EquipmentManagment.Device.Options.clsEndPointOptions()),
                LdUldState = EQControl.LDULD_STATE.UNLOAD
            });
            keyFrame.EQControls.Add(new EQControl()
            {
                EQ = new clsEQ(new EquipmentManagment.Device.Options.clsEndPointOptions()),
                LdUldState = EQControl.LDULD_STATE.NO_REQUEST
            });

            var keyFrame2 = new KeyFrame(TimeSpan.FromSeconds(10));

            Console.WriteLine(keyFrame.Time);
            Console.WriteLine(keyFrame2.Time);
        }
    }
}