using System;
using UnityEngine;

namespace Generics
{
    public abstract class SingletonComponent<T> : MonoBehaviour where T : MonoBehaviour
    {
        public static T Instance
        {
            get
            {
                if (!s_instance)
                {
                    s_instance = FindFirstObjectByType<T>();

                    if (!s_instance)
                    {
                        throw new ArgumentNullException(nameof(s_instance));
                    }
                }

                return s_instance;
            }
        }

        private static T s_instance = default;
    }
}