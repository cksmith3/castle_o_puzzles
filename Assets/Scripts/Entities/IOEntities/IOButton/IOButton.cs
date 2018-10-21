﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[AddComponentMenu("IOEntities/IOButton")]
public class IOButton : IOEntity, IUsable
{
    public DigitalState pressed;

    public void Use() {
        // Send a quick impulse
        pressed.impulse();
    }
}
