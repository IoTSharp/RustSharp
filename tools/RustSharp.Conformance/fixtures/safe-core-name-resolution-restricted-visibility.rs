pub mod outer {
    pub mod inner {
        pub(crate) fn wide() {}
        pub(super) fn parent() {}
        pub(in crate::outer) fn ancestor() {}
        pub(self) fn own() {}
        mod child { fn check() { super::own(); } }
    }
    fn check() { inner::parent(); inner::ancestor(); }
}
fn main() { outer::inner::wide(); }
