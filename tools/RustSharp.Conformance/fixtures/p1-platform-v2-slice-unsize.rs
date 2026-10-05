// frozen P1 fixture: slice-unsize
fn main() {
    let values = [3, 5, 7];
    let view: &[i32] = &values;
    println!("{}", view.len());
    println!("{}", view[1]);
}
