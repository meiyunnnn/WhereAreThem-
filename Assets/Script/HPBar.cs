using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PlayerHPBar : MonoBehaviour
{
    [Header("References")]
    public Image hpBarFill;
    public TMP_Text hpText;

    [Header("Settings")]
    public int maxHP = 100;

    // เรียกจาก PlayerStateSync เมื่อ HP เปลี่ยน
    public void UpdateHP(int currentHP)
    {
        float ratio = Mathf.Clamp01((float)currentHP / maxHP);
        hpBarFill.fillAmount = ratio;
        hpText.text = $"{currentHP}/{maxHP}";
    }
}