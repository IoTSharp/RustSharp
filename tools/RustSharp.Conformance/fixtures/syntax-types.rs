type Unit = ();
type Never = !;
type Pair = (i32, bool);
type Single = (i32,);
type Group = (i32);
type Refs = &&'static mut [i32];
type Array = [crate::model::Item; 1 + 2];
